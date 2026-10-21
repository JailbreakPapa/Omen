// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

namespace Omen.Core.Diagnostics;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback synchronously, on the reporting
/// thread, the instant <see cref="Report"/> is called.
///
/// This exists because the framework's <see cref="Progress{T}"/> always marshals through a
/// captured <see cref="SynchronizationContext"/> - falling back to
/// <c>ThreadPool.QueueUserWorkItem</c> when none is captured (the case for every console app
/// and every xUnit test) - and so gives no guarantee that a reported value has been observed
/// by the time the async operation that reported it has completed. Under thread-pool pressure
/// the queued callback can lag arbitrarily behind the awaited Task's completion, and in a
/// one-shot process it can be silently dropped entirely if the process exits before the
/// ThreadPool gets around to it.
///
/// Use this type wherever the caller needs "reported means observed" semantics - e.g. a CLI
/// command that must print every event before the process exits, or a test that asserts on the
/// accumulated event list right after awaiting the call that produced it. Keep
/// <see cref="Progress{T}"/> only where asynchronous marshaling onto another thread is actually
/// the intent, such as a GUI updating bound UI state from a background thread.
/// </summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
