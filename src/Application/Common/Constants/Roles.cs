// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace CleanArchitecture.Blazor.Application.Common.Constants;

/// <summary>
/// The roles the provisioner creates. Keep this list and
/// <c>ApplicationDbContextInitializer.EnsureRolesAsync</c> in step - a constant here that nobody
/// creates is a gate that can never be satisfied.
/// <para>
/// GX divergence from upstream: a third role, <c>Users</c>, was added in Pass 3 §F to fix an
/// upstream bug where three navigation entries were gated on a role the seeder never created. Pass
/// 7-2 deleted those entries with the demo features, which left <c>Users</c> seeded, granted
/// exactly the same claims as <c>Basic</c>, and gating nothing at all. Two indistinguishable roles
/// is worse than the bug was, so the constant, its seeding and its grant are removed here.
/// </para>
/// </summary>
public abstract class Roles
{
    public const string Admin = nameof(Admin);
    public const string Basic = nameof(Basic);
} 
