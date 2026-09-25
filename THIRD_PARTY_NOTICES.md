# Third-party components

Godox Desktop is Copyright (c) 2026 m9agrest and licensed under the [MIT License](LICENSE).
Third-party components retain their own copyright and license notices.

## godox-mesh-bt / ha-godox-mesh

- Source: https://github.com/binary-person/ha-godox-mesh
- Pinned revision: `f07d872c6f34a656ed935c120b9b679bf0378fbd`
- License: MIT
- Copyright (c) 2026 Matt Gibson
- Copyright (c) 2026 Simon Cheng
- Original license: [docs/licenses/ha-godox-mesh-LICENSE.txt](docs/licenses/ha-godox-mesh-LICENSE.txt)

The dependency supplies the Bluetooth Mesh protocol implementation and is installed
by pip. Its source is not vendored in this repository. The dependency's original
copyright notices are preserved; Godox Desktop's license does not replace them.

## Other runtime dependencies

- [Bleak](https://github.com/hbldh/bleak) — MIT.
- [cryptography](https://github.com/pyca/cryptography) — Apache-2.0 OR BSD-3-Clause.
- [.NET](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf),
  [ASP.NET Core](https://github.com/dotnet/aspnetcore) — MIT, with their own notices.

Development dependencies are installed separately. Release bundles include .NET,
CPython and the Python packages. The Python distribution's `LICENSE.txt`, package
license files under `runtime/python/Lib/site-packages/*.dist-info`, and the .NET
publish license/notice files are retained in the distribution.

- [CPython](https://www.python.org/) — Python Software Foundation License, with
  additional notices in its bundled `LICENSE.txt`.
- [Godox icon](assets/NOTICE.md) — third-party brand artwork from the official Godox
  website; it is not licensed under this project's MIT license.

When redistributing packaged binaries, retain all supplied license and notice files,
including notices for transitive dependencies.

Godox Desktop is an independent project, not an official Godox product.
