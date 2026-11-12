## What changes and why

## How it was checked

- [ ] `dotnet build PlaIO.slnx -c Release` has no warnings
- [ ] `dotnet test PlaIO.slnx -c Release` passes
- [ ] Output of `plaio info`, `plaio blocks` and `plaio directory` on the same files is identical before and after, or the differences are listed and intended
- [ ] No Archicad files, customer data or vendor files are added
