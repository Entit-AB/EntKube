namespace EntKube.Web.Data;

/// <summary>One snapshot entry in the visual editor's auto-saved version history.</summary>
public class ThemeHistoryEntry
{
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    public ThemeVariables Variables { get; set; } = new();
}


/// <summary>
/// Structured visual properties for a Keycloak named theme.
/// Serialized as JSON and stored in the vault under the key
/// <c>named-theme-{themeId}-variables</c>. Used by the visual theme editor
/// to generate the <c>login.css</c> file that is deployed to Keycloak.
/// </summary>
public class ThemeVariables
{
    // ── Colors ────────────────────────────────────────────────────────────────
    public string PrimaryColor { get; set; } = "#0066CC";
    public string PrimaryColorDark { get; set; } = "#004E99";
    public string PageBackground { get; set; } = "#f4f4f4";
    public string CardBackground { get; set; } = "#ffffff";
    public string TextColor { get; set; } = "#151515";
    public string MutedTextColor { get; set; } = "#6a6e73";
    public string LinkColor { get; set; } = "#0066CC";
    public string InputBorderColor { get; set; } = "#8a8d90";
    public string InputBackground { get; set; } = "#ffffff";
    public string InputTextColor { get; set; } = "#151515";
    public string ButtonTextColor { get; set; } = "#ffffff";
    public string ErrorColor { get; set; } = "#c9190b";
    public string HeaderBackground { get; set; } = "#151515";
    public string HeaderTextColor { get; set; } = "#ffffff";

    // ── Typography ────────────────────────────────────────────────────────────
    public string FontFamily { get; set; } = "system-ui, -apple-system, sans-serif";
    public int FontSizePx { get; set; } = 14;

    // ── Shape ─────────────────────────────────────────────────────────────────
    public int ButtonRadiusPx { get; set; } = 3;
    public int InputRadiusPx { get; set; } = 3;
    public int CardRadiusPx { get; set; } = 4;
    /// <summary>Maximum width of the login card in pixels (300–900).</summary>
    public int CardMaxWidthPx { get; set; } = 500;
    /// <summary>Card drop-shadow level: 0=none 1=subtle 2=medium 3=strong.</summary>
    public int CardShadowLevel { get; set; } = 1;

    // ── Logo ──────────────────────────────────────────────────────────────────
    public string? LogoResourceName { get; set; }
    public string? LogoExternalUrl { get; set; }
    public int LogoHeightPx { get; set; } = 48;
    public bool ShowLogo { get; set; } = true;

    // ── Favicon ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Name of an uploaded resource to publish as the realm's favicon. It is deployed
    /// to <c>resources/img/favicon.ico</c> — the fixed path the Keycloak login template
    /// references — so it shows in the browser tab for this realm's login pages.
    /// Managed Keycloak only (requires deploying theme files).
    /// </summary>
    public string? FaviconResourceName { get; set; }

    // ── Background ────────────────────────────────────────────────────────────
    public bool UseBackgroundImage { get; set; } = false;
    public string? BackgroundImageResourceName { get; set; }

    // ── Footer ────────────────────────────────────────────────────────────────
    public bool ShowFooter { get; set; } = false;
    public string FooterText { get; set; } = "";

    // ── Extra CSS ─────────────────────────────────────────────────────────────
    // Appended verbatim after the auto-generated CSS on every save.
    public string ExtraCss { get; set; } = "";

    // ── Base theme ────────────────────────────────────────────────────────────
    /// <summary>
    /// Which Keycloak base theme this customisation targets.
    /// "keycloak.v2" = PatternFly 5 selectors (Keycloak 20+, current default).
    /// "keycloak"    = Classic Bootstrap + PatternFly 4 selectors (Keycloak 19 and earlier).
    /// </summary>
    public string BaseTheme { get; set; } = "keycloak.v2";
}
