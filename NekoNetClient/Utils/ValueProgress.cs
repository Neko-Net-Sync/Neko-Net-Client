/*
    Neko-Net Client — Utils.ValueProgress
    ------------------------------------
    Purpose
    - Small helper that exposes the most recently reported progress value while still invoking callbacks
      via the base <see cref="Progress{T}"/> type.
*/
namespace NekoNetClient.Utils;

/// <summary>
/// A <see cref="Progress{T}"/> implementation that stores the last observed value for pull-style checks.
/// </summary>
public class ValueProgress<T> : Progress<T>
{
    /// <summary>The last value reported to the progress object.</summary>
    public T? Value { get; set; }

    /// <inheritdoc />
    protected override void OnReport(T value)
    {
        base.OnReport(value);
        Value = value;
    }

    /// <summary>Reports a new value (shortcut for OnReport).</summary>
    public void Report(T value)
    {
        OnReport(value);
    }

    /// <summary>Clears the last observed value.</summary>
    public void Clear()
    {
        Value = default;
    }
}
