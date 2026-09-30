// The game sheet's left column. Only markup of its own: each button hands its click to the sheet it sits in.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AmdNr.App;

public partial class SheetSide : UserControl
{
    public SheetSide() => InitializeComponent();

    private GameSheet Sheet => this.FindAncestorOfType<GameSheet>()!;

    private void OnPlay(object? sender, RoutedEventArgs e) => Sheet.OnPlay(sender, e);
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => Sheet.OnOpenFolder(sender, e);
    private void OnRemoveGame(object? sender, RoutedEventArgs e) => Sheet.OnRemoveGame(sender, e);
    private void OnPickCover(object? sender, RoutedEventArgs e) => Sheet.OnPickCover(sender, e);
    private void OnResetCover(object? sender, RoutedEventArgs e) => Sheet.OnResetCover(sender, e);
    private void OnAutoUpdateChanged(object? sender, RoutedEventArgs e) => Sheet.OnAutoUpdateChanged(sender, e);
    private void OnExportSettings(object? sender, RoutedEventArgs e) => Sheet.OnExportSettings(sender, e);
    private void OnImportSettings(object? sender, RoutedEventArgs e) => Sheet.OnImportSettings(sender, e);
}
