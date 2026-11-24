# Bundled networking components

These binaries were copied unchanged from the user's existing Alien Link project:
`AlienLink/kernel/ETLanCore.exe` and `AlienLink/kernel/wintun.dll`.
No download from an unofficial mirror was performed.

| File | SHA-256 |
| --- | --- |
| ETLanCore.exe | 286FDBCFFE54D04701D3B826F8FC3DD2EE43FE7D79B7BA816EBC12F9C322696E |
| wintun.dll | E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE |

ETLanCore is a third-party executable using the external platform at `https://etlan.xyz.fun`.
The supplied project does not include a verifiable license granting public redistribution of this kernel or platform/API usage rights.
Wintun also needs its applicable distribution license and notices from its supplier.
Presence in this local test build is not evidence of permission for a public release.
Before distributing, obtain/verify the relevant licenses and platform terms and include the required notices.

The C# authentication, engine status parsing and peer-name logic in `PCL.Link/Core` is adapted from the user's existing Alien Link implementation.
The PCL native page, named-pipe integration, Minecraft discovery bridge and loopback proxy are maintained in this repository.
