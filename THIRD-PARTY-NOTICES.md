# Third-party dependencies

This source project depends on .NET/WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite (including its SQLite native dependencies), LibVLCSharp.WPF, VideoLAN.LibVLC.Windows, and xUnit/testing packages. Exact direct/transitive versions are recorded in the project files and `packages.lock.json` files.

The application includes the **Cascadia Mono** font by Microsoft (`src/MediaWorkbench.App/Fonts/CascadiaMono.ttf`, built into the executable), licensed under the SIL Open Font License 1.1; the license text is in `src/MediaWorkbench.App/Fonts/OFL.txt` and is copied next to the published executable as `Fonts/OFL.txt`.

FFmpeg and FFprobe are external prerequisites and are not included in this repository or the generated application ZIP. FFmpeg capabilities and distribution terms depend on the particular build used.

Before distributing compiled releases, review the upstream license and notice files for every included native/managed dependency, retain required notices, and determine the appropriate license for this application's own code. The repository intentionally does not choose an application license on the owner's behalf. Publishing a ZIP is not a license-compliance audit.
