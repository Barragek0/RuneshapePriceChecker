# Sentry Native runtime

The runtime files are built from the pinned Sentry Native 0.16.2 source archive by `build.ps1`.
The application embeds `sentry.dll`, `crashpad_handler.exe`, `crashpad_wer.dll`, and their hash manifest, then extracts the runtime and its database under the `Sentry` directory beside the application executable when automatic crash reporting is enabled.

The build uses the Windows Crashpad backend and WinHTTP transport. Native PDBs are retained under `obj/SentryNative/symbols` for Sentry symbol upload and are never shipped to users.
