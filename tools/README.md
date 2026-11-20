# tools/

Development-only harnesses. They are **not** part of the shipped plugin and are
excluded from the project in `src/QuickLook.Plugin.SaiViewer.csproj`
(`<Compile Remove="tools\**" />`).

They live here as source because they need the plugin's internal types, so the
simplest way to run them is to compile them together with `src/*.cs`:

```powershell
# Render the panel for every .sai2 in a folder to PNG (used for visual regression)
csc -nologo -noconfig -nostdlib -langversion:latest -target:exe -platform:anycpu `
    -out:Harness.exe `
    -r:<path-to>\mscorlib.dll -r:<path-to>\System.dll -r:<path-to>\System.Core.dll `
    -r:<path-to>\WPF\WindowsBase.dll -r:<path-to>\WPF\PresentationCore.dll `
    -r:<path-to>\WPF\PresentationFramework.dll `
    tools\Harness\Harness.cs src\SaiViewer\*.cs
Harness.exe -o out\panels "C:\path\to\*.sai2"
```

`Compare/Compare.cs` follows the same recipe and reports how many bytes differ
between the parallel/box-downsampled decoder and the plain reference decoder —
this is how the tiled parallel version was proven equivalent (0 mismatches over
4- and 3-channel files, up to 4096×4096).

Both need `<path-to>` = `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`, since the
plugin targets .NET Framework.
