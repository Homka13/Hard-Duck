# Hard-Duck Workspace Memory & Rules

This document contains key architectural decisions, resolved bugs, and rules for agents working on the Hard-Duck project.

## WPF Single-File Publish & MaterialDesignThemes Gotchas

### Problem 1: Assembly Preloading
When publishing this WPF application as a single-file self-contained executable (`PublishSingleFile=true`, `SelfContained=true`), the app may crash on startup with a `XamlParseException` or `FileNotFoundException`. This happens because WPF merges ResourceDictionaries from `MaterialDesignThemes.Wpf` and `MaterialDesignColors` in `App.xaml` at startup before any C# code has executed to trigger the loading of these bundled DLLs into the `AppDomain`.

### Solution & Rule 1
Always ensure that `MaterialDesignThemes.Wpf` and `MaterialDesignColors` assemblies are preloaded inside the constructor of the `App` class in `src/App.xaml.cs` **before** `InitializeComponent()` is called:
```csharp
public App()
{
    // Force load Material Design assemblies into AppDomain for single-file publish
    _ = typeof(MaterialDesignThemes.Wpf.Card);
    _ = typeof(MaterialDesignColors.Swatch);
}
```

### Problem 2: Brittle Pack URIs & XAML Geometry Compiler Issues
Manual merging of individual color files (e.g. `MaterialDesignColor.Amber.xaml`) via `pack://` URIs in `App.xaml` is brittle and prone to path changes across major version bumps (like v5.x). Additionally, using `TransformedGeometry` in XAML causes the compiler error `MC3074` because it lacks standard XAML parser support under the default presentation namespace.

### Solution & Rule 2
- Always use `materialDesign:BundledTheme` in `App.xaml` to configure the primary/secondary themes:
  ```xml
  <materialDesign:BundledTheme BaseTheme="Dark" PrimaryColor="Amber" SecondaryColor="Amber" />
  ```
- If a geometry needs scaling, translation, or rotation, use `PathGeometry` with its `Transform` property in XAML rather than `TransformedGeometry`:
  ```xml
  <PathGeometry x:Key="DuckGeometry" Figures="...">
      <PathGeometry.Transform>
          <TransformGroup>
              <ScaleTransform ScaleY="-1"/>
              <TranslateTransform Y="480.36"/>
          </TransformGroup>
      </PathGeometry.Transform>
  </PathGeometry>
  ```

### Problem 3: XAML Parsing Event Initialization (NullReferenceException)
When WPF parses elements sequentially in `InitializeComponent()`, any toggle containing `IsChecked="True"` triggers the `Checked` event (e.g. `Checked="OnToggleChanged"`) immediately, BEFORE the rest of the controls are instantiated. If `OnToggleChanged` references other controls in the window, it throws a `NullReferenceException` and crashes the application silently on startup.

### Solution & Rule 3
Always add a null check for all referenced WPF controls at the beginning of event handlers that are triggered during the initialization phase (like `OnToggleChanged` / `UpdateVisibleStages`):
```csharp
private void UpdateVisibleStages()
{
    if (SecureBootToggle == null || UsbToggle == null || BiosToggle == null || LapsToggle == null || HardDuckToggle == null)
        return;
    // ...
}
```

---

## Local Compilation
To compile the single-file executable locally, run the root-level [build.ps1](file:///d:/Git%20projects/Hard-Duck/build.ps1) script or run the following command:
```powershell
dotnet publish src/Hard-Duck.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

---

## Unit Testing WPF & CsvReporter

### WPF Dependency in Tests
The test project [Hard-Duck.Tests.csproj](file:///d:/Git%20projects/Hard-Duck/tests/Hard-Duck.Tests/Hard-Duck.Tests.csproj) targets `net8.0-windows` and has `<UseWPF>true</UseWPF>` enabled. This is required because tests reference types from WPF and `MaterialDesignThemes.Wpf`.

### CsvReporter Testing
`CsvReporter` writes hardening results to a system-wide directory (`C:\ProgramData\ITSecurity`).
To prevent unit tests from corrupting or archiving real status reports:
1. `CsvReporter.Directory` has been refactored into a static property.
2. In the test class constructor/initialize, always override `CsvReporter.Directory` to a secure temp path (e.g. `Path.Combine(Path.GetTempPath(), ...)`).
3. Clean up the temp directory after tests complete (e.g. in `Dispose()`).
