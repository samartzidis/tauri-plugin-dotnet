//! Macros that replace the per-project backend setup in `src-tauri/src/lib.rs`.
//!
//! They are macros, not functions, because the debug path and the embedded bundle are resolved at compile
//! time relative to the calling crate (`env!("CARGO_MANIFEST_DIR")`, `include_bytes!`). Every path starts
//! from `CARGO_MANIFEST_DIR`, so it is absolute: `include_bytes!` would resolve a relative path against this
//! crate's file, not the caller's.
//!
//! They assume the layout the rest of the setup (`beforeDevCommand`, `beforeBuildCommand`, the build
//! properties) already assumes: the backend project in `../src-dotnet/<project>/` next to `src-tauri`,
//! building into `bin/<Configuration>/<tfm>/`. Anything else (a `RuntimeIdentifier`, artifacts output, a
//! custom `OutputPath`) should build the host with [`init_with`](crate::init_with) and its own path.
//!
//! Every macro takes the project folder name, then optionally, in either order:
//! - `assembly = "..."`: the assembly name, when it differs from the folder name;
//! - `tfm = "..."`: the target framework, when it is not `net8.0`.
//!
//! ```ignore
//! tauri_plugin_dotnet::backend!("MyApp.Backend")
//! tauri_plugin_dotnet::backend!("MyApp.Backend", assembly = "MyApp", tfm = "net9.0")
//! ```

/// The whole plugin for the backend project `$project`, loaded from files. Goes straight on the
/// app's builder, with no `setup` hook:
///
/// ```ignore
/// tauri::Builder::default()
///   .plugin(tauri_plugin_dotnet::backend!("MyApp.Backend"))
/// ```
///
/// In a debug build, this runs the backend as a separate dev-only sidecar process
/// ([`SidecarHost`](crate::SidecarHost)): a rebuild restarts just that process, not the whole app, so
/// `tauri dev` never reloads the frontend on a C# change. A release build loads it in-process
/// ([`HostfxrHost`](crate::HostfxrHost)) exactly as before. For the embedded mode, switching between
/// the two ([`any_backend_host!`]), or a different layout, use [`init_with`](crate::init_with) directly.
#[macro_export]
macro_rules! backend {
  ($($args:tt)+) => {
    $crate::init_with(|app| $crate::__tdn_args!(__tdn_backend_host (app,) $($args)+))
  };
}

/// [`HostfxrOptions`](crate::HostfxrOptions) that load the backend project `$project` from files: in a
/// debug build from the project's own Debug output (the plugin loads a copy, so `dotnet build` can replace
/// it while the app runs), otherwise from `resource_dir()/dotnet/`, where `bundle.resources` ships it. The
/// release path does not depend on the target framework: the Release build is written flat into
/// `src-tauri/backend` by `beforeBuildCommand`.
///
/// ```ignore
/// tauri_plugin_dotnet::init_with(|app| HostfxrHost::new(tauri_plugin_dotnet::backend_options!(app, "MyApp.Backend")))
/// ```
#[macro_export]
macro_rules! backend_options {
  ($app:expr, $($args:tt)+) => {
    $crate::__tdn_args!(__tdn_backend_options ($app,) $($args)+)
  };
}

/// [`HostfxrOptions`](crate::HostfxrOptions) that embed the bundle of the backend project `$project`
/// (`dotnet build -p:TauriDotNetEmbed=true`) from the Debug or Release build output, matching the cargo
/// profile. Fails to compile until that bundle exists, so keep it behind a Cargo feature.
///
/// ```ignore
/// tauri_plugin_dotnet::init_with(|_app| HostfxrHost::new(tauri_plugin_dotnet::embedded_backend_options!("MyApp.Backend")))
/// ```
#[macro_export]
macro_rules! embedded_backend_options {
  ($($args:tt)+) => {
    $crate::__tdn_args!(__tdn_embedded_backend_options () $($args)+)
  };
}

/// [`embedded_backend_options!`] when the caller's own Cargo feature named exactly `embedded-backend` is
/// on, [`backend_options!`] otherwise. For an app that switches between the two; an app that uses one
/// mode should call that macro directly. The feature must be declared in the caller's `[features]` table
/// even while it is off, or Cargo's `unexpected_cfgs` lint warns.
///
/// This returns [`HostfxrOptions`](crate::HostfxrOptions) either way, for composing a [`HostfxrHost`](crate::HostfxrHost)
/// by hand; it does not get the dev-only sidecar host, since embedded mode never uses it (there is no
/// dev-loop story for an embedded bundle) and picking `HostfxrHost::new(...)` here is precisely what
/// says "always in-process" for the non-embedded case too. Use [`any_backend_host!`] instead to also
/// get the sidecar host in debug builds.
///
/// ```ignore
/// tauri_plugin_dotnet::init_with(|app| HostfxrHost::new(tauri_plugin_dotnet::any_backend_options!(app, "MyApp.Backend")))
/// ```
#[macro_export]
macro_rules! any_backend_options {
  ($app:expr, $($args:tt)+) => {{
    #[cfg(feature = "embedded-backend")]
    {
      // The embedded form needs no app handle; touch it so the caller's variable is not unused.
      let _ = &$app;
      $crate::embedded_backend_options!($($args)+)
    }
    #[cfg(not(feature = "embedded-backend"))]
    {
      $crate::backend_options!($app, $($args)+)
    }
  }};
}

/// Like [`any_backend_options!`], but returns a fully constructed host instead of options for the
/// caller to wrap: [`embedded_backend_options!`] wrapped in [`HostfxrHost`](crate::HostfxrHost) when the
/// caller's `embedded-backend` feature is on, otherwise the same host [`backend!`] builds (a
/// [`SidecarHost`](crate::SidecarHost) in debug, [`HostfxrHost`](crate::HostfxrHost) in release). Use this,
/// rather than `HostfxrHost::new(any_backend_options!(...))`, to get the dev-only sidecar host on an app
/// that also switches into embedded mode:
///
/// ```ignore
/// tauri_plugin_dotnet::init_with(|app| tauri_plugin_dotnet::any_backend_host!(app, "MyApp.Backend"))
/// ```
#[macro_export]
macro_rules! any_backend_host {
  ($app:expr, $($args:tt)+) => {{
    #[cfg(feature = "embedded-backend")]
    {
      let _ = &$app;
      $crate::HostfxrHost::new($crate::embedded_backend_options!($($args)+))
    }
    #[cfg(not(feature = "embedded-backend"))]
    {
      $crate::__tdn_args!(__tdn_backend_host ($app,) $($args)+)
    }
  }};
}

/// Reads `"<project>" [, assembly = "..."] [, tfm = "..."]` (keys in either order, trailing comma allowed)
/// and calls `$callback!(<prefix> project, assembly, tfm)` with the defaults filled in.
#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_args {
  ($callback:ident ($($prefix:tt)*) $project:literal $(,)?) => {
    $crate::$callback!($($prefix)* $project, $project, "net8.0")
  };
  ($callback:ident ($($prefix:tt)*) $project:literal, tfm = $tfm:literal $(,)?) => {
    $crate::$callback!($($prefix)* $project, $project, $tfm)
  };
  ($callback:ident ($($prefix:tt)*) $project:literal, assembly = $assembly:literal $(,)?) => {
    $crate::$callback!($($prefix)* $project, $assembly, "net8.0")
  };
  ($callback:ident ($($prefix:tt)*) $project:literal, assembly = $assembly:literal, tfm = $tfm:literal $(,)?) => {
    $crate::$callback!($($prefix)* $project, $assembly, $tfm)
  };
  ($callback:ident ($($prefix:tt)*) $project:literal, tfm = $tfm:literal, assembly = $assembly:literal $(,)?) => {
    $crate::$callback!($($prefix)* $project, $assembly, $tfm)
  };
}

#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_backend_options {
  ($app:expr, $project:literal, $assembly:literal, $tfm:literal) => {{
    #[cfg(debug_assertions)]
    fn __tauri_plugin_dotnet_options<R: ::tauri::Runtime>(
      _app: &::tauri::AppHandle<R>,
    ) -> $crate::HostfxrOptions {
      $crate::HostfxrOptions::new(::std::path::PathBuf::from($crate::__tdn_debug_dll!(
        $project, $assembly, $tfm
      )))
    }

    #[cfg(not(debug_assertions))]
    fn __tauri_plugin_dotnet_options<R: ::tauri::Runtime>(
      app: &::tauri::AppHandle<R>,
    ) -> $crate::HostfxrOptions {
      use ::tauri::Manager as _;
      let dir = app.path().resource_dir().expect("no resource directory");
      $crate::HostfxrOptions::new(dir.join($crate::__tdn_release_dll!($project, $assembly, $tfm)))
    }

    __tauri_plugin_dotnet_options($app)
  }};
}

/// A [`SidecarHost`](crate::SidecarHost) from the debug build output in a debug build, a
/// [`HostfxrHost`](crate::HostfxrHost) via [`__tdn_backend_options!`] otherwise. Backs [`backend!`] and
/// the non-embedded branch of [`any_backend_host!`].
#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_backend_host {
  ($app:expr, $project:literal, $assembly:literal, $tfm:literal) => {{
    #[cfg(debug_assertions)]
    fn __tauri_plugin_dotnet_host<R: ::tauri::Runtime>(_app: &::tauri::AppHandle<R>) -> $crate::SidecarHost {
      $crate::SidecarHost::new(
        $crate::SidecarOptions::new(
          ::std::path::PathBuf::from($crate::__tdn_debug_csproj!($project, $assembly, $tfm)),
          $assembly,
        )
        .tfm($tfm),
      )
    }

    #[cfg(not(debug_assertions))]
    fn __tauri_plugin_dotnet_host<R: ::tauri::Runtime>(app: &::tauri::AppHandle<R>) -> $crate::HostfxrHost {
      $crate::HostfxrHost::new($crate::__tdn_backend_options!(app, $project, $assembly, $tfm))
    }

    __tauri_plugin_dotnet_host($app)
  }};
}

#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_embedded_backend_options {
  ($project:literal, $assembly:literal, $tfm:literal) => {{
    #[cfg(debug_assertions)]
    {
      $crate::HostfxrOptions::embedded(include_bytes!($crate::__tdn_bundle!(
        "Debug", $project, $assembly, $tfm
      )))
    }
    #[cfg(not(debug_assertions))]
    {
      $crate::HostfxrOptions::embedded(include_bytes!($crate::__tdn_bundle!(
        "Release", $project, $assembly, $tfm
      )))
    }
  }};
}

/// The Debug build output of the backend, as an absolute path in the calling crate.
#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_debug_dll {
  ($project:literal, $assembly:literal, $tfm:literal) => {
    concat!(
      env!("CARGO_MANIFEST_DIR"),
      "/../src-dotnet/",
      $project,
      "/bin/Debug/",
      $tfm,
      "/",
      $assembly,
      ".dll"
    )
  };
}

/// The backend's own project file, as an absolute path in the calling crate. The `.csproj` file name
/// follows the project folder, per .NET convention, regardless of `$assembly`.
#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_debug_csproj {
  ($project:literal, $assembly:literal, $tfm:literal) => {
    concat!(env!("CARGO_MANIFEST_DIR"), "/../src-dotnet/", $project, "/", $project, ".csproj")
  };
}

/// Where `bundle.resources` puts the backend, relative to `resource_dir()`.
#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_release_dll {
  ($project:literal, $assembly:literal, $tfm:literal) => {
    concat!("dotnet/", $assembly, ".dll")
  };
}

/// The embedded bundle of one configuration, as an absolute path in the calling crate.
#[doc(hidden)]
#[macro_export]
macro_rules! __tdn_bundle {
  ($configuration:literal, $project:literal, $assembly:literal, $tfm:literal) => {
    concat!(
      env!("CARGO_MANIFEST_DIR"),
      "/../src-dotnet/",
      $project,
      "/bin/",
      $configuration,
      "/",
      $tfm,
      "/",
      $assembly,
      ".tdnbundle"
    )
  };
}

#[cfg(test)]
mod tests {
  const ROOT: &str = env!("CARGO_MANIFEST_DIR");

  #[test]
  fn the_project_name_is_also_the_assembly_name_and_net8_the_default() {
    assert_eq!(
      crate::__tdn_args!(__tdn_debug_dll () "MyApp.Backend"),
      format!("{ROOT}/../src-dotnet/MyApp.Backend/bin/Debug/net8.0/MyApp.Backend.dll")
    );
    assert_eq!(crate::__tdn_args!(__tdn_release_dll () "MyApp.Backend"), "dotnet/MyApp.Backend.dll");
  }

  #[test]
  fn the_assembly_name_names_the_file_and_the_project_the_folder() {
    assert_eq!(
      crate::__tdn_args!(__tdn_debug_dll () "backend", assembly = "MyApp"),
      format!("{ROOT}/../src-dotnet/backend/bin/Debug/net8.0/MyApp.dll")
    );
    assert_eq!(crate::__tdn_args!(__tdn_release_dll () "backend", assembly = "MyApp"), "dotnet/MyApp.dll");
    assert_eq!(
      crate::__tdn_args!(__tdn_bundle ("Release",) "backend", assembly = "MyApp"),
      format!("{ROOT}/../src-dotnet/backend/bin/Release/net8.0/MyApp.tdnbundle")
    );
  }

  #[test]
  fn the_target_framework_changes_only_the_build_output_folder() {
    assert_eq!(
      crate::__tdn_args!(__tdn_debug_dll () "MyApp.Backend", tfm = "net9.0"),
      format!("{ROOT}/../src-dotnet/MyApp.Backend/bin/Debug/net9.0/MyApp.Backend.dll")
    );
    assert_eq!(
      crate::__tdn_args!(__tdn_release_dll () "MyApp.Backend", tfm = "net9.0"),
      "dotnet/MyApp.Backend.dll"
    );
    assert_eq!(
      crate::__tdn_args!(__tdn_bundle ("Debug",) "MyApp.Backend", tfm = "net9.0"),
      format!("{ROOT}/../src-dotnet/MyApp.Backend/bin/Debug/net9.0/MyApp.Backend.tdnbundle")
    );
  }

  #[test]
  fn the_keys_can_come_in_either_order_with_a_trailing_comma() {
    let expected = format!("{ROOT}/../src-dotnet/backend/bin/Debug/net9.0/MyApp.dll");
    assert_eq!(crate::__tdn_args!(__tdn_debug_dll () "backend", assembly = "MyApp", tfm = "net9.0"), expected);
    assert_eq!(crate::__tdn_args!(__tdn_debug_dll () "backend", tfm = "net9.0", assembly = "MyApp"), expected);
    assert_eq!(crate::__tdn_args!(__tdn_debug_dll () "backend", tfm = "net9.0", assembly = "MyApp",), expected);
    assert_eq!(
      crate::__tdn_args!(__tdn_debug_dll () "MyApp.Backend",),
      format!("{ROOT}/../src-dotnet/MyApp.Backend/bin/Debug/net8.0/MyApp.Backend.dll")
    );
  }
}
