# Notes for agents working on cap-compress

Read [README.md](README.md) for what the app does, how it's built and how the tests run.

- C# WinForms on .NET Framework 4.x, compiled by `build.cmd` with the `csc.exe` that ships with
  Windows: no project file, no NuGet, nothing to install. Windows only.
- Quick tests: the size-planning checks in `tests/EngineTest.cs` (no video). Full checks: the app
  and both test programs compile with warnings as errors. The real-video tests (encodes, the
  window test) need a video: Olex runs those himself.
- Tests run only in the background: windows open off-screen without focus, no input sent to the
  desktop, settings kept in the test's own folder. Keep new tests the same way.
