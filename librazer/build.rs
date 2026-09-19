use std::{env, fs, path::PathBuf};
fn main() {
    let directory =
        PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap()).join("../devices/laptops");
    println!("cargo:rerun-if-changed={}", directory.display());
    let mut files: Vec<_> = fs::read_dir(&directory)
        .expect("device registry directory")
        .map(|e| e.unwrap().path())
        .filter(|p| p.extension().is_some_and(|e| e == "json"))
        .collect();
    files.sort();
    assert!(!files.is_empty(), "device registry is empty");
    let records: Vec<serde_json::Value> = files
        .iter()
        .map(|p| {
            serde_json::from_str(&fs::read_to_string(p).expect("read device record"))
                .expect("valid device JSON")
        })
        .collect();
    let output = PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("device_registry.json");
    fs::write(output, serde_json::to_vec(&records).unwrap()).expect("write compiled registry");
}
