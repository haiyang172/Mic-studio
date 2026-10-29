namespace MicStudio.NativeApo;

/// <summary>Pure review rules, separated from the registry so rejection cases are tested without system writes.</summary>
public static class NativeApoDeploymentPolicy
{
    public static string? Rejection(NativeApoFileIdentity component, bool endpointActive,
        NativeRegistryData? existingEffect, NativeRegistryData? existingModes,
        bool classOccupied, bool audioRegistrationOccupied)
    {
        if (!component.Present || !component.NativeX64 || !component.Trusted || !component.ContractExport) return component.Message;
        if (!endpointActive) return "The selected capture endpoint is not active.";
        if (existingEffect != null || existingModes != null)
            return existingEffect?.Text != null && Guid.TryParse(existingEffect.Text, out var id) && id == NativeApoDeploymentService.ClassId
                ? "This endpoint is already bound. Use parameter updates or restore the previous deployment."
                : "This endpoint already has an SFX or processing-mode registration. It will be preserved; automatic replacement is blocked.";
        if (classOccupied || audioRegistrationOccupied) return "The APO class is already registered. Restore the previous deployment before reinstalling.";
        return null;
    }
}
