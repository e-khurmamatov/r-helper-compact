//! Compiled laptop allowlist. Parsing and resolution never open HID devices.
use anyhow::{anyhow, bail, ensure, Result};
use serde::{Deserialize, Serialize};
use std::sync::OnceLock;
use crate::profile::BladeGeneration;

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Laptop {
    pub schema_version: u8,
    pub name: String,
    pub pid: String,
    pub sku_prefixes: Vec<String>,
    pub enabled: bool,
    pub protocol: String,
    pub keyboard_protocol: String,
    pub verification: String,
    pub source: String,
    pub notes: String,
}
impl Laptop {
    pub fn product_id(&self) -> u16 { u16::from_str_radix(&self.pid,16).expect("validated PID") }
    pub fn generation(&self) -> BladeGeneration {
        match self.protocol.as_str() { "Legacy4"=>BladeGeneration::Legacy4,"Modern6"=>BladeGeneration::Modern6,_=>unreachable!("validated protocol") }
    }
}
pub fn parse(data: &str) -> Result<Vec<Laptop>> {
    let records: Vec<Laptop>=serde_json::from_str(data)?;
    ensure!(!records.is_empty(),"Registry is empty");
    let mut pids=std::collections::HashSet::new();
    for r in &records {
        ensure!(r.schema_version==1 && !r.name.trim().is_empty(),"Invalid registry version/name");
        ensure!(r.pid.len()==4 && r.pid.bytes().all(|b|b.is_ascii_hexdigit()),"Invalid PID");
        ensure!(r.product_id()!=0 && pids.insert(r.product_id()),"Duplicate/zero PID");
        ensure!(!r.sku_prefixes.is_empty(),"Missing exact chassis identifiers");
        for sku in &r.sku_prefixes {
            ensure!(sku.len()==9 && sku.starts_with("RZ09-") && sku[5..].bytes().all(|b|b.is_ascii_digit()),"SKU must be RZ09- plus four digits");
        }
        ensure!(["Legacy4","Modern6"].contains(&r.protocol.as_str()),"Unimplemented protocol");
        ensure!(["none","standard_matrix_ff"].contains(&r.keyboard_protocol.as_str()),"Unimplemented keyboard protocol");
        ensure!(["user_confirmed","upstream_profile","candidate"].contains(&r.verification.as_str()),"Invalid verification status");
        ensure!(!r.enabled || r.verification!="candidate","Candidates cannot enable writes");
        ensure!(r.source.starts_with("https://") && !r.notes.trim().is_empty(),"Missing provenance");
    }
    Ok(records)
}
pub fn records() -> Result<&'static [Laptop]> {
    static DATA: OnceLock<std::result::Result<Vec<Laptop>,String>>=OnceLock::new();
    DATA.get_or_init(||parse(include_str!(concat!(env!("OUT_DIR"), "/device_registry.json"))).map_err(|e|e.to_string()))
        .as_ref().map(|v|v.as_slice()).map_err(|e|anyhow!("Invalid device registry: {e}"))
}
pub fn lookup(pid:u16) -> Option<&'static Laptop> {
    records().ok()?.iter().find(|r|r.enabled && r.product_id()==pid)
}
pub fn select<'a>(records:&'a [Laptop],pids:&[u16],sku:&str)->Result<&'a Laptop> {
    let matches:Vec<_>=records.iter().filter(|r|r.enabled && pids.contains(&r.product_id()) && r.sku_prefixes.iter().any(|prefix|sku.starts_with(prefix))).collect();
    if matches.len()!=1 { bail!("Unsupported model: no unique SKU and HID PID match in the registry. Controls are disabled."); }
    Ok(matches[0])
}
// The callback represents the very first operation that can open/write HID.
pub fn open_registered<T>(pids:&[u16],sku:&str,open:impl FnOnce(&'static Laptop)->Result<T>)->Result<T> {
    let model=select(records()?,pids,sku)?;
    open(model)
}
#[derive(Debug, Clone, Serialize)]
pub struct SupportReport {
    pub supported: bool,
    pub sku: String,
    pub hid: Vec<String>,
    pub reason: String,
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn bundled_registry_is_valid() { assert!(!records().unwrap().is_empty()); }
    #[test] fn unknown_mismatched_disabled_and_missing_devices_never_open() {
        for (pids,sku) in [(vec![0xffff],"RZ09-0528"),(vec![0x028c],"RZ09-9999"),(vec![0x029c],"RZ09-0485"),(vec![],"RZ09-0427"),(vec![0x028c],"OTHER-PC")] {
            let mut opened=false;
            let result=open_registered(&pids,sku,|_|{opened=true;Ok(())});
            assert!(result.is_err());assert!(!opened);
        }
    }
    #[test] fn supported_laptop_selected_independently_of_external_peripheral_order() {
        for pids in [vec![0xffff,0x028c,0x0080],vec![0x028c,0x0080,0xffff]] {
            let pid=open_registered(&pids,"RZ09-0427NE",|r|Ok(r.product_id())).unwrap();
            assert_eq!(pid,0x028c);
        }
    }
    #[test] fn malformed_duplicate_and_unimplemented_records_fail_closed() {
        let data=include_str!(concat!(env!("OUT_DIR"), "/device_registry.json"));
        let mut v:serde_json::Value=serde_json::from_str(data).unwrap();
        let first=v[0].clone();v.as_array_mut().unwrap().push(first);
        assert!(parse(&v.to_string()).is_err());
        assert!(parse(&data.replace("Legacy4","UnknownFamily")).is_err());
        assert!(parse(&data.replace("RZ09-0427","RZ09-")).is_err());
        assert!(parse("[]").is_err());
    }
    #[test] fn ambiguous_laptop_match_is_rejected() {
        let mut r=parse(include_str!(concat!(env!("OUT_DIR"), "/device_registry.json"))).unwrap();
        for model in &mut r { model.sku_prefixes=vec!["RZ09-0427".into()]; }
        assert!(select(&r,&[0x028c,0x028b],"RZ09-0427NE").is_err());
    }
}
