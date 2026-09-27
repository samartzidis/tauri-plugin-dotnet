//! The backend bundle: every file an in-process .NET backend needs, in one blob that can be
//! embedded in the app's executable with `include_bytes!`.
//!
//! The bundle is produced at build time by `Tauri.Plugin.DotNet.Generator bundle` (the
//! `TauriDotNetEmbed` MSBuild option) and read here. All integers are little-endian:
//!
//! ```text
//! magic          8 bytes  "TDNBNDL1"
//! backend name   u32 length + UTF-8   simple name of the backend assembly (for example "MyApp.Backend")
//! entry count    u32
//! entries        kind u8, name (u32 length + UTF-8), data (u32 length + bytes)
//! ```
//!
//! Entry kinds: `1` the backend's `runtimeconfig.json` (the name is ignored), `2` a managed
//! assembly (`Name.dll`), `3` its debug symbols (`Name.pdb`, optional).

/// First bytes of every bundle; the trailing digit is the format version.
pub(crate) const MAGIC: &[u8; 8] = b"TDNBNDL1";

const KIND_RUNTIME_CONFIG: u8 = 1;
const KIND_ASSEMBLY: u8 = 2;
const KIND_SYMBOLS: u8 = 3;

/// The assembly that contains the native entry points; every bundle must carry it.
const HOST_ASSEMBLY: &str = "Tauri.Plugin.DotNet";

/// A parsed bundle. Everything borrows from the embedded bytes.
#[derive(Debug)]
pub(crate) struct Bundle<'a> {
  /// Simple name of the backend assembly, used by .NET to find it among the loaded assemblies.
  pub backend: &'a str,
  /// Contents of the backend's `runtimeconfig.json`.
  pub runtime_config: &'a [u8],
  pub assemblies: Vec<BundledAssembly<'a>>,
}

#[derive(Debug)]
pub(crate) struct BundledAssembly<'a> {
  /// File name without the extension.
  pub name: &'a str,
  pub bytes: &'a [u8],
  pub symbols: Option<&'a [u8]>,
}

struct Reader<'a> {
  data: &'a [u8],
  position: usize,
}

impl<'a> Reader<'a> {
  fn take(&mut self, count: usize, what: &str) -> Result<&'a [u8], String> {
    let end = self
      .position
      .checked_add(count)
      .filter(|end| *end <= self.data.len())
      .ok_or_else(|| format!("it is truncated (reading {what})"))?;
    let slice = &self.data[self.position..end];
    self.position = end;
    Ok(slice)
  }

  fn u8(&mut self, what: &str) -> Result<u8, String> {
    Ok(self.take(1, what)?[0])
  }

  fn u32(&mut self, what: &str) -> Result<u32, String> {
    let bytes = self.take(4, what)?;
    Ok(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
  }

  fn text(&mut self, what: &str) -> Result<&'a str, String> {
    let length = self.u32(what)? as usize;
    std::str::from_utf8(self.take(length, what)?).map_err(|_| format!("{what} is not valid UTF-8"))
  }

  fn blob(&mut self, what: &str) -> Result<&'a [u8], String> {
    let length = self.u32(what)? as usize;
    self.take(length, what)
  }
}

/// Reads a bundle, checking that it is complete enough to start a backend from.
pub(crate) fn parse(data: &[u8]) -> Result<Bundle<'_>, String> {
  let mut reader = Reader { data, position: 0 };

  if reader.take(MAGIC.len(), "the header").ok() != Some(MAGIC.as_slice()) {
    return Err("it does not start with the expected header (was it made by a different version of the tool?)".into());
  }

  let backend = reader.text("the backend name")?;
  if backend.is_empty() {
    return Err("the backend name is empty".into());
  }

  let count = reader.u32("the entry count")?;

  let mut runtime_config = None;
  let mut assemblies: Vec<BundledAssembly<'_>> = Vec::new();
  let mut symbols: Vec<(&str, &[u8])> = Vec::new();

  for _ in 0..count {
    let kind = reader.u8("an entry kind")?;
    let name = reader.text("an entry name")?;
    let bytes = reader.blob(name)?;

    match kind {
      KIND_RUNTIME_CONFIG => {
        if runtime_config.replace(bytes).is_some() {
          return Err("it has more than one runtime config".into());
        }
      }
      KIND_ASSEMBLY => {
        let stem = name.strip_suffix(".dll").ok_or_else(|| format!("the assembly '{name}' does not end in .dll"))?;
        if assemblies.iter().any(|a| a.name.eq_ignore_ascii_case(stem)) {
          return Err(format!("the assembly '{stem}' appears twice"));
        }
        assemblies.push(BundledAssembly { name: stem, bytes, symbols: None });
      }
      KIND_SYMBOLS => {
        let stem = name.strip_suffix(".pdb").ok_or_else(|| format!("the symbols '{name}' do not end in .pdb"))?;
        symbols.push((stem, bytes));
      }
      other => return Err(format!("entry '{name}' has the unknown kind {other}")),
    }
  }

  if reader.position != data.len() {
    return Err("it has unexpected data after the last entry".into());
  }

  for (stem, bytes) in symbols {
    let assembly = assemblies
      .iter_mut()
      .find(|a| a.name.eq_ignore_ascii_case(stem))
      .ok_or_else(|| format!("the symbols '{stem}.pdb' have no assembly"))?;
    if assembly.symbols.replace(bytes).is_some() {
      return Err(format!("the assembly '{stem}' has two sets of symbols"));
    }
  }

  let runtime_config = runtime_config.ok_or("it has no runtime config")?;
  if !assemblies.iter().any(|a| a.name.eq_ignore_ascii_case(HOST_ASSEMBLY)) {
    return Err(format!("it does not contain {HOST_ASSEMBLY}.dll (does the backend reference the {HOST_ASSEMBLY} package?)"));
  }

  Ok(Bundle { backend, runtime_config, assemblies })
}

#[cfg(test)]
pub(crate) mod tests {
  use super::*;

  /// The bundle the C# writer test also produces byte for byte: `B` with a runtime config, two
  /// assemblies and the symbols of `B`. Keeping the same literal on both sides pins the format.
  #[rustfmt::skip]
  pub(crate) const GOLDEN: &[u8] = &[
    0x54, 0x44, 0x4E, 0x42, 0x4E, 0x44, 0x4C, 0x31,                         // "TDNBNDL1"
    0x01, 0x00, 0x00, 0x00, 0x42,                                           // backend "B"
    0x04, 0x00, 0x00, 0x00,                                                 // 4 entries
    0x01, 0x12, 0x00, 0x00, 0x00,                                           // runtime config, name length 18
      0x72, 0x75, 0x6E, 0x74, 0x69, 0x6D, 0x65, 0x63, 0x6F, 0x6E, 0x66, 0x69, 0x67, 0x2E, 0x6A, 0x73, 0x6F, 0x6E, // "runtimeconfig.json"
      0x02, 0x00, 0x00, 0x00, 0x7B, 0x7D,                                   // "{}"
    0x02, 0x05, 0x00, 0x00, 0x00, 0x42, 0x2E, 0x64, 0x6C, 0x6C,             // assembly "B.dll"
      0x01, 0x00, 0x00, 0x00, 0x03,
    0x02, 0x17, 0x00, 0x00, 0x00,                                           // assembly, name length 23
      0x54, 0x61, 0x75, 0x72, 0x69, 0x2E, 0x50, 0x6C, 0x75, 0x67, 0x69, 0x6E, 0x2E, 0x44, 0x6F, 0x74, 0x4E, 0x65, 0x74, 0x2E, 0x64, 0x6C, 0x6C, // "Tauri.Plugin.DotNet.dll"
      0x02, 0x00, 0x00, 0x00, 0x01, 0x02,
    0x03, 0x05, 0x00, 0x00, 0x00, 0x42, 0x2E, 0x70, 0x64, 0x62,             // symbols "B.pdb"
      0x01, 0x00, 0x00, 0x00, 0x04,
  ];

  /// Builds a bundle from `(kind, name, data)` entries.
  fn build(backend: &str, entries: &[(u8, &str, &[u8])]) -> Vec<u8> {
    let mut out = MAGIC.to_vec();
    out.extend((backend.len() as u32).to_le_bytes());
    out.extend(backend.as_bytes());
    out.extend((entries.len() as u32).to_le_bytes());
    for (kind, name, data) in entries {
      out.push(*kind);
      out.extend((name.len() as u32).to_le_bytes());
      out.extend(name.as_bytes());
      out.extend((data.len() as u32).to_le_bytes());
      out.extend(*data);
    }
    out
  }

  fn valid_entries() -> Vec<(u8, &'static str, &'static [u8])> {
    vec![
      (1, "runtimeconfig.json", b"{}"),
      (2, "B.dll", &[3]),
      (2, "Tauri.Plugin.DotNet.dll", &[1, 2]),
    ]
  }

  fn error_of(data: &[u8]) -> String {
    parse(data).unwrap_err()
  }

  #[test]
  fn the_golden_bundle_parses_into_its_parts() {
    let bundle = parse(GOLDEN).unwrap();

    assert_eq!(bundle.backend, "B");
    assert_eq!(bundle.runtime_config, b"{}");
    assert_eq!(bundle.assemblies.len(), 2);
    assert_eq!(bundle.assemblies[0].name, "B");
    assert_eq!(bundle.assemblies[0].bytes, [3]);
    assert_eq!(bundle.assemblies[0].symbols, Some(&[4u8][..]));
    assert_eq!(bundle.assemblies[1].name, "Tauri.Plugin.DotNet");
    assert_eq!(bundle.assemblies[1].bytes, [1, 2]);
    assert_eq!(bundle.assemblies[1].symbols, None);
  }

  #[test]
  fn the_test_builder_matches_the_golden_bytes() {
    let built = build(
      "B",
      &[
        (1, "runtimeconfig.json", b"{}"),
        (2, "B.dll", &[3]),
        (2, "Tauri.Plugin.DotNet.dll", &[1, 2]),
        (3, "B.pdb", &[4]),
      ],
    );

    assert_eq!(built, GOLDEN);
  }

  #[test]
  fn symbols_are_matched_to_their_assembly_ignoring_case_and_entry_order() {
    let mut entries = valid_entries();
    entries.insert(0, (3, "b.pdb", &[9])); // before its assembly, and a different case

    let data = build("B", &entries);
    let bundle = parse(&data).unwrap();

    assert_eq!(bundle.assemblies[0].symbols, Some(&[9u8][..]));
  }

  #[test]
  fn a_valid_bundle_without_symbols_is_accepted() {
    let data = build("B", &valid_entries());

    assert!(parse(&data).unwrap().assemblies.iter().all(|a| a.symbols.is_none()));
  }

  #[test]
  fn a_wrong_header_is_rejected() {
    assert!(error_of(b"NOTABUNDLE").contains("expected header"));
    assert!(error_of(b"").contains("expected header"));

    let mut other_version = build("B", &valid_entries());
    other_version[7] = b'2';
    assert!(error_of(&other_version).contains("expected header"));
  }

  #[test]
  fn every_truncation_is_rejected_without_panicking() {
    let full = GOLDEN;
    for length in 0..full.len() {
      assert!(parse(&full[..length]).is_err(), "a bundle cut to {length} bytes must not parse");
    }
  }

  #[test]
  fn a_length_larger_than_the_data_is_rejected() {
    // The first entry's data length (at offset 8+5+4+1+4+18) claims 4 GiB.
    let mut data = GOLDEN.to_vec();
    let offset = 8 + 5 + 4 + 1 + 4 + 18;
    data[offset..offset + 4].copy_from_slice(&u32::MAX.to_le_bytes());

    assert!(error_of(&data).contains("truncated"));
  }

  #[test]
  fn trailing_bytes_are_rejected() {
    let mut data = GOLDEN.to_vec();
    data.push(0);

    assert!(error_of(&data).contains("after the last entry"));
  }

  #[test]
  fn an_empty_backend_name_is_rejected() {
    assert!(error_of(&build("", &valid_entries())).contains("backend name is empty"));
  }

  #[test]
  fn a_missing_runtime_config_is_rejected() {
    let data = build("B", &[(2, "B.dll", &[3]), (2, "Tauri.Plugin.DotNet.dll", &[1])]);

    assert!(error_of(&data).contains("no runtime config"));
  }

  #[test]
  fn two_runtime_configs_are_rejected() {
    let mut entries = valid_entries();
    entries.push((1, "again.json", b"{}"));

    assert!(error_of(&build("B", &entries)).contains("more than one runtime config"));
  }

  #[test]
  fn a_missing_host_assembly_is_rejected_with_a_hint() {
    let data = build("B", &[(1, "runtimeconfig.json", b"{}"), (2, "B.dll", &[3])]);

    let error = error_of(&data);
    assert!(error.contains("Tauri.Plugin.DotNet.dll"), "{error}");
    assert!(error.contains("reference"), "{error}");
  }

  #[test]
  fn duplicate_assemblies_are_rejected_ignoring_case() {
    let mut entries = valid_entries();
    entries.push((2, "b.dll", &[5]));

    assert!(error_of(&build("B", &entries)).contains("appears twice"));
  }

  #[test]
  fn symbols_without_an_assembly_are_rejected() {
    let mut entries = valid_entries();
    entries.push((3, "Ghost.pdb", &[1]));

    assert!(error_of(&build("B", &entries)).contains("have no assembly"));
  }

  #[test]
  fn two_sets_of_symbols_for_one_assembly_are_rejected() {
    let mut entries = valid_entries();
    entries.push((3, "B.pdb", &[1]));
    entries.push((3, "B.pdb", &[2]));

    assert!(error_of(&build("B", &entries)).contains("two sets of symbols"));
  }

  #[test]
  fn entries_with_the_wrong_extension_or_kind_are_rejected() {
    let mut wrong_dll = valid_entries();
    wrong_dll.push((2, "Native.so", &[1]));
    assert!(error_of(&build("B", &wrong_dll)).contains("does not end in .dll"));

    let mut wrong_pdb = valid_entries();
    wrong_pdb.push((3, "B.symbols", &[1]));
    assert!(error_of(&build("B", &wrong_pdb)).contains("do not end in .pdb"));

    let mut unknown = valid_entries();
    unknown.push((9, "X", &[1]));
    assert!(error_of(&build("B", &unknown)).contains("unknown kind 9"));
  }

  #[test]
  fn text_that_is_not_utf8_is_rejected() {
    let mut data = build("B", &valid_entries());
    data[8 + 4] = 0xFF; // the backend name

    assert!(error_of(&data).contains("not valid UTF-8"));
  }
}
