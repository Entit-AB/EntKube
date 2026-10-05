using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Model configuration for the <see cref="EntKube.Web.Modules.Module.Connectivity"/> module —
/// Routes, network policy, service mesh, mTLS and VPN.
///
/// <para>Moved verbatim out of <see cref="ApplicationDbContext.OnModelCreating"/>, which
/// had grown to 163 entity blocks in one method with nothing marking where one module's
/// tables ended and the next began. Which module owns a table is decided by
/// <see cref="EntKube.Web.Modules.ModuleMap"/>, and <c>ModuleBoundaryTests</c> keeps the
/// two from drifting apart.</para>
/// </summary>
internal static class ConnectivityModel
{
    internal static void Configure(ModelBuilder builder)
    {
        // Table names, stated rather than inferred.
        //
        // EF derives a table name from the DbSet property on whichever context declares it,
        // so "AlertIncident" became "AlertIncidents" only because ApplicationDbContext spells
        // that property in the plural. The per-module contexts do not declare each other's
        // sets, which means that without this block each of them maps the same entity to a
        // different table — and a query crossing a module boundary would hit a table that is
        // not there. Naming them here makes the schema a property of the model instead of a
        // property of C# property naming.
        builder.Entity<AppL4Route>().ToTable("AppL4Routes");
        builder.Entity<AppNetworkPolicy>().ToTable("AppNetworkPolicies");
        builder.Entity<AppRoute>().ToTable("AppRoutes");
        builder.Entity<ClientCaBundle>().ToTable("ClientCaBundles");
        builder.Entity<ClientCaCertificate>().ToTable("ClientCaCertificates");
        builder.Entity<ConnectivityRule>().ToTable("ConnectivityRules");
        builder.Entity<ExternalRoute>().ToTable("ExternalRoutes");
        builder.Entity<ExternalRouteHealthHistory>().ToTable("ExternalRouteHealthHistories");
        builder.Entity<MeshMtlsPolicy>().ToTable("MeshMtlsPolicies");
        builder.Entity<OutboundMtlsCredential>().ToTable("OutboundMtlsCredentials");
        builder.Entity<VpnLocalEndpoint>().ToTable("VpnLocalEndpoints");
        builder.Entity<VpnRemoteEndpoint>().ToTable("VpnRemoteEndpoints");
        builder.Entity<VpnTunnel>().ToTable("VpnTunnels");

        // ExternalRoute — exposes a component via Gateway API HTTPRoute.
        // Hostname must be unique within a cluster (enforced via component→cluster).

        builder.Entity<ExternalRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Hostname).HasMaxLength(253).IsRequired();
            entity.Property(r => r.ServiceName).HasMaxLength(200);
            entity.Property(r => r.PathPrefix).HasMaxLength(200);
            entity.Property(r => r.ClusterIssuerName).HasMaxLength(200);
            entity.Property(r => r.GatewayName).HasMaxLength(200);
            entity.Property(r => r.GatewayNamespace).HasMaxLength(63);
            entity.Property(r => r.TlsMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.SessionAffinity).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.SessionAffinityKey).HasMaxLength(200);

            entity.HasOne(r => r.Component)
                .WithMany(c => c.ExternalRoutes)
                .HasForeignKey(r => r.ComponentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ExternalRouteHealthHistory>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.HasIndex(h => new { h.RouteId, h.CheckedAt });

            entity.HasOne(h => h.Route)
                .WithMany()
                .HasForeignKey(h => h.RouteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VpnTunnel — scoped to a tenant, name unique within a tenant.

        builder.Entity<VpnTunnel>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.TunnelType).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.IkeProposal).HasMaxLength(100).IsRequired();
            entity.Property(t => t.EspProposal).HasMaxLength(100).IsRequired();

            entity.HasOne(t => t.Tenant)
                .WithMany()
                .HasForeignKey(t => t.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // VpnLocalEndpoint — a platform cluster participating in a VPN tunnel.

        builder.Entity<VpnLocalEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Subnets).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.PublicIp).HasMaxLength(45);
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Tunnel)
                .WithMany(t => t.LocalEndpoints)
                .HasForeignKey(e => e.VpnTunnelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Cluster)
                .WithMany()
                .HasForeignKey(e => e.ClusterId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Component)
                .WithMany()
                .HasForeignKey(e => e.ComponentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // VpnRemoteEndpoint — an external site for SiteToSite tunnels. PSK/cert stored
        // as VaultSecret entries scoped to this endpoint via VpnRemoteEndpointId.

        builder.Entity<VpnRemoteEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PublicIp).HasMaxLength(45).IsRequired();
            entity.Property(e => e.Subnets).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.AuthMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.LocalId).HasMaxLength(500);
            entity.Property(e => e.RemoteId).HasMaxLength(500);

            entity.HasOne(e => e.Tunnel)
                .WithMany(t => t.RemoteEndpoints)
                .HasForeignKey(e => e.VpnTunnelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppNetworkPolicy — many per app per environment; name unique within (app, environment).
        builder.Entity<AppNetworkPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.AppId, p.EnvironmentId, p.Name }).IsUnique();
            entity.Property(p => p.Name).HasMaxLength(63).IsRequired();
            entity.Property(p => p.PolicyType).HasConversion<string>().HasMaxLength(30);
            entity.Property(p => p.AllowFromNamespace).HasMaxLength(63);

            entity.HasOne(p => p.App)
                .WithMany(a => a.NetworkPolicies)
                .HasForeignKey(p => p.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(p => p.Environment)
                .WithMany()
                .HasForeignKey(p => p.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AppRoute — app-level hostname + TLS config. Cascades from App.

        builder.Entity<AppRoute>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Hostname).HasMaxLength(253).IsRequired();
            entity.Property(r => r.TlsMode).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.ClusterIssuerName).HasMaxLength(200);
            // Existing routes predate management gating — default them to managed.
            entity.Property(r => r.IsManaged).HasDefaultValue(true);

            entity.HasOne(r => r.App)
                .WithMany(a => a.Routes)
                .HasForeignKey(r => r.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: deleting a trust anchor that routes still authenticate
            // against would silently drop those routes' only client check. The delete is
            // blocked until the routes are moved off it or stop requiring a certificate.
            entity.HasOne(r => r.ClientCaBundle)
                .WithMany(b => b.Routes)
                .HasForeignKey(r => r.ClientCaBundleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // MeshMtlsPolicy — service-to-service mTLS posture per cluster namespace.

        builder.Entity<MeshMtlsPolicy>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Namespace).HasMaxLength(63).IsRequired();
            entity.Property(p => p.Mode).HasConversion<string>().HasMaxLength(20);

            // One posture per namespace: two rows would render two namespace-wide
            // PeerAuthentications and leave which one wins up to Istio.
            entity.HasIndex(p => new { p.ClusterId, p.Namespace }).IsUnique();
        });

        // OutboundMtlsCredential — client certificates a customer app presents to partner APIs.

        builder.Entity<OutboundMtlsCredential>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Host).HasMaxLength(253).IsRequired();
            entity.Property(c => c.Mode).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.Port).HasDefaultValue(443);

            // The name becomes the Secret/ServiceEntry/DestinationRule name in the app's
            // namespace, so it has to be unique within the app.
            entity.HasIndex(c => new { c.AppId, c.Name }).IsUnique();
        });

        // ClientCaBundle — CA trust anchors for inbound client-certificate authentication.
        // Public CA material only; no encryption, same as CaTrustBundleSource.

        builder.Entity<ClientCaBundle>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Name).HasMaxLength(200).IsRequired();
            entity.Property(b => b.Description).HasMaxLength(1000);
            entity.Property(b => b.ListenerPort).HasDefaultValue(Services.MtlsService.DefaultListenerPort);

            // One name per tenant — the name is how an operator picks a trust anchor on a route.
            entity.HasIndex(b => new { b.TenantId, b.Name }).IsUnique();
        });

        builder.Entity<ClientCaCertificate>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Pem).IsRequired();
            entity.Property(c => c.Subject).HasMaxLength(500);
            entity.Property(c => c.Fingerprint).HasMaxLength(100);

            entity.HasOne(c => c.Bundle)
                .WithMany(b => b.Certificates)
                .HasForeignKey(c => c.BundleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AppL4Route — raw TCP/UDP port exposure through the dedicated Istio L4 gateway.
        // Cascades from AppDeployment only (single cascade path App → AppDeployment → AppL4Route);
        // AppId is a plain scoping column so the unique index can enforce per-cluster port ownership.

        builder.Entity<AppL4Route>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Protocol).HasConversion<string>().HasMaxLength(10);
            entity.Property(r => r.ServiceName).HasMaxLength(200).IsRequired();
            entity.Property(r => r.GatewayName).HasMaxLength(200);
            entity.Property(r => r.GatewayNamespace).HasMaxLength(63);
            entity.Property(r => r.IsManaged).HasDefaultValue(true);
            entity.HasIndex(r => r.AppId);

            entity.HasOne(r => r.AppDeployment)
                .WithMany()
                .HasForeignKey(r => r.AppDeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ConnectivityRule — a directed least-privilege edge (this app ↔ a peer).
        builder.Entity<ConnectivityRule>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.AppId, r.EnvironmentId });
            entity.Property(r => r.Direction).HasConversion<string>().HasMaxLength(10);
            entity.Property(r => r.PeerType).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Source).HasConversion<string>().HasMaxLength(20);
            entity.Property(r => r.Protocol).HasConversion<string>().HasMaxLength(10);
            entity.Property(r => r.PeerNamespace).HasMaxLength(63);
            entity.Property(r => r.PeerCidr).HasMaxLength(64);
            entity.Property(r => r.AppProtocol).HasMaxLength(30);

            entity.HasOne(r => r.App)
                .WithMany(a => a.ConnectivityRules)
                .HasForeignKey(r => r.AppId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.Environment)
                .WithMany()
                .HasForeignKey(r => r.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.PeerApp)
                .WithMany()
                .HasForeignKey(r => r.PeerAppId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
