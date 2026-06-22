namespace Savedrake.App.ViewModels
{
    // A row in the Characters tab. Plain getters (no INotifyPropertyChanged): rows are rebuilt by RefreshCharacters(),
    // so any state change replaces the instance rather than mutating it.
    public sealed class CharacterRow
    {
        public string Name { get; set; }
        public int FileCount { get; set; }
        public bool IsActive { get; set; }   // == ActiveCharacter (gold dot/name + "Current" pill)
        public bool IsPlaying { get; set; }  // == LoadedCharacter ("Playing" pill, SuccessBrush)
        public string CountLabel => FileCount + (FileCount == 1 ? " backup" : " backups");

        // The Backups-tab switcher ComboBox uses a custom control template whose selection box shows the item directly,
        // so it falls back to ToString(); return the character name rather than the type name.
        public override string ToString() => Name;
    }
}
