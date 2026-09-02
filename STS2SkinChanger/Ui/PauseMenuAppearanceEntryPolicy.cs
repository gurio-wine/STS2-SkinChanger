namespace STS2SkinChanger.Ui;

internal readonly record struct PauseMenuAppearanceEntryDecision(
    bool CreateButton,
    bool ShowButton);

internal static class PauseMenuAppearanceEntryPolicy
{
    internal static PauseMenuAppearanceEntryDecision Resolve(
        bool showEntry,
        bool buttonExists) =>
        new(
            CreateButton: showEntry && !buttonExists,
            ShowButton: showEntry);
}
