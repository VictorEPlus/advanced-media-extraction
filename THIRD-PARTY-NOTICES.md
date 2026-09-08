# Third-party dependencies

This source project depends on .NET/WPF, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite (including its SQLite native dependencies), LibVLCSharp.WPF, VideoLAN.LibVLC.Windows, and xUnit/testing packages. Exact direct/transitive versions are recorded in the project files and `packages.lock.json` files.

FFmpeg and FFprobe are external prerequisites and are not included in this repository or the generated application ZIP. FFmpeg capabilities and distribution terms depend on the particular build used.

Before distributing compiled releases, review the upstream license and notice files for every included native/managed dependency, retain required notices, and determine the appropriate license for this application's own code. The repository intentionally does not choose an application license on the owner's behalf. Publishing a ZIP is not a license-compliance audit.
