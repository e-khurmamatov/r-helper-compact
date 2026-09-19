param([Parameter(Mandatory)][string]$AppDirectory,[Parameter(Mandatory)][string]$OutputFile)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$AppDirectory=(Resolve-Path -LiteralPath $AppDirectory).Path
$Namespace='http://schemas.microsoft.com/wix/2006/wi'
[xml]$Product=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/Product.wxs') -Raw
$Fixed=@($Product.SelectNodes('//*[local-name()="File"]') | ForEach-Object { $_.Source.Replace('$(var.AppDir)\','').Replace('\','/') })
$Doc=[xml]"<Wix xmlns='$Namespace'><Fragment><DirectoryRef Id='INSTALLFOLDER'/><ComponentGroup Id='RuntimeFiles'/></Fragment></Wix>"
$Dirs=@{''=$Doc.Wix.Fragment.DirectoryRef}
function New-Id([string]$Prefix,[string]$Value) {
    $Sha=[Security.Cryptography.SHA256]::Create()
    try { return $Prefix + [Convert]::ToHexString($Sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Substring(0,28) } finally { $Sha.Dispose() }
}
function Get-DirectoryNode([string]$Relative) {
    if ($Dirs.ContainsKey($Relative)) { return $Dirs[$Relative] }
    $Parent=[IO.Path]::GetDirectoryName($Relative).Replace('\','/')
    $ParentNode=Get-DirectoryNode $Parent
    $Node=$Doc.CreateElement('Directory',$Namespace)
    $Node.SetAttribute('Id',(New-Id 'D' $Relative));$Node.SetAttribute('Name',[IO.Path]::GetFileName($Relative))
    [void]$ParentNode.AppendChild($Node);$Dirs[$Relative]=$Node
    return $Node
}
foreach ($File in Get-ChildItem -LiteralPath $AppDirectory -File -Recurse | Sort-Object FullName) {
    $Relative=[IO.Path]::GetRelativePath($AppDirectory,$File.FullName).Replace('\','/')
    if ($Fixed -contains $Relative -or $File.Extension -eq '.pdb' -or $Relative -eq 'SHA256SUMS.txt') { continue }
    $Parent=Get-DirectoryNode ([IO.Path]::GetDirectoryName($Relative).Replace('\','/'))
    $Id=New-Id 'C' $Relative
    $Component=$Doc.CreateElement('Component',$Namespace);$Component.SetAttribute('Id',$Id);$Component.SetAttribute('Guid','*');$Component.SetAttribute('Win64','yes')
    $Entry=$Doc.CreateElement('File',$Namespace);$Entry.SetAttribute('Id',(New-Id 'F' $Relative));$Entry.SetAttribute('Source',('$(var.AppDir)\'+$Relative.Replace('/','\')));$Entry.SetAttribute('KeyPath','yes')
    [void]$Component.AppendChild($Entry);[void]$Parent.AppendChild($Component)
    $Ref=$Doc.CreateElement('ComponentRef',$Namespace);$Ref.SetAttribute('Id',$Id);[void]$Doc.Wix.Fragment.ComponentGroup.AppendChild($Ref)
}
$Doc.Save($OutputFile)
