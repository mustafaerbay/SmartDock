# ADR 0001 — Native WPF + MVVM, widgets as view models

**Status:** accepted

## Context
The dock runs all day, must be cheap when idle, and must integrate with Win32 (window enumeration,
taskbar, shell hooks). A browser-based UI would add a Chromium process and ~150 MB of memory.

## Decision
- WPF on .NET 10 (`net10.0-windows10.0.19041.0`) with CommunityToolkit.Mvvm and Microsoft DI.
- Widgets live in `SmartDock.Widgets` (plain `net10.0`, no WPF reference) and are **view models**
  implementing `IDockWidget`. The app renders each through an implicit `DataTemplate`
  (`Themes/WidgetTemplates.xaml`) inside a shared `WidgetCard` chrome. Widgets never create windows.
- OS access goes through interfaces in `SmartDock.Core` (`ISystemMetrics`, `IMediaService`,
  `ISecretStore`), implemented in `SmartDock.Windows`. This keeps widgets unit-testable on any OS.
- Widgets communicate only via `IEventBus` / `INotificationService`, never by referencing each other.
- Settings/manager windows use WPF's built-in Fluent theme (`ThemeMode.System`); the dock uses its own templates.

## Consequences
- Core, Infrastructure and Widgets tests run anywhere; Windows-specific code is isolated.
- Property-change notifications from background refreshes are fine for scalar properties (WPF marshals
  them). Collections are exposed as immutable snapshots; `ICommand.CanExecuteChanged` is marshalled via
  `WidgetBase.PostToUi`.
