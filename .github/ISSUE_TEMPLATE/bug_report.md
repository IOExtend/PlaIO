---
name: Bug report
about: Something is read wrongly, or the tool fails
labels: bug
---

**PlaIO version** (`plaio --version` or the package version):

**Archicad version that saved the file** (and its kind: `.pla`, `.pln` or `.bimx`):

**Command line or API call** (remove confidential values):

**Output of `plaio info <file>`** (if it runs):

**First 64 bytes of the file in hex** (for example `xxd -l 64 <file>` or, in PowerShell 7, `Format-Hex -Path <file> -Count 64`):

**What you expected**:

**What happened** (error messages from standard error, exit code):

Please do not attach Archicad or BIMx files with confidential content, and never a whole model. The header bytes and a short description of the file are enough to start.
