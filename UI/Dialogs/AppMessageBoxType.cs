namespace SimpleDroneGCS.UI.Dialogs
{
    public enum AppMessageBoxType
    {
        Info,
        Success,
        Warning,
        Error,
        Confirm
    }

    // Новый enum для YesNoCancel
    public enum AppMessageBoxResult
    {
        Yes,
        No,
        Cancel
    }
}