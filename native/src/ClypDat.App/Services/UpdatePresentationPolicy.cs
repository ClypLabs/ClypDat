namespace ClypDat.App.Services;

internal enum UpdateCheckPresentation
{
    BadgeOnly,
    Dialog,
}

internal static class UpdatePresentationPolicy
{
    public static UpdateCheckPresentation ForAutomaticCheck() => UpdateCheckPresentation.BadgeOnly;

    public static UpdateCheckPresentation ForUserAction() => UpdateCheckPresentation.Dialog;
}
