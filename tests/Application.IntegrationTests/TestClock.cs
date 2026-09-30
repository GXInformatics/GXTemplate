using System;
using CleanArchitecture.Blazor.Application.Common.Interfaces;

namespace CleanArchitecture.Blazor.Application.IntegrationTests;

/// <summary>
/// The application's <see cref="IDateTime"/>, settable by a test (pass 47, CO-165). Real UTC time
/// unless <see cref="Set"/> has been called; <c>Testing.ResetState</c> calls <see cref="Reset"/>.
/// </summary>
/// <remarks>
/// Registered as the application registers its clock: scoped, returning this one instance. A
/// singleton registration would let a scoped service resolved from the root provider pass unnoticed
/// in the harness while the application's scope validation refused it.
/// </remarks>
public sealed class TestClock : IDateTime
{
    private DateTime? _fixed;

    public DateTime UtcNow => _fixed ?? DateTime.UtcNow;

    public void Set(DateTime utc) => _fixed = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

    public void Reset() => _fixed = null;
}
