namespace NxtUI.Web.Services;

/// <summary>
/// Marker for an <see cref="NxtUI.Core.Services.IRunService"/> that already publishes
/// live events to <see cref="RunEventBroker"/> itself (see <see cref="MockRunService"/>).
/// <see cref="RunEventWatcher"/> skips backends that implement this — polling them
/// for "new" events would just republish what they already pushed.
/// </summary>
public interface IPushesOwnRunEvents;
