package provider

import (
	"context"
	"os"

	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/provider"
	"github.com/hashicorp/terraform-plugin-framework/provider/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/types"
)

type entkubeProvider struct{}

func New() provider.Provider { return &entkubeProvider{} }

func (p *entkubeProvider) Metadata(_ context.Context, _ provider.MetadataRequest, resp *provider.MetadataResponse) {
	resp.TypeName = "entkube"
}

type providerModel struct {
	URL   types.String `tfsdk:"url"`
	Token types.String `tfsdk:"token"`
}

func (p *entkubeProvider) Schema(_ context.Context, _ provider.SchemaRequest, resp *provider.SchemaResponse) {
	resp.Schema = schema.Schema{
		MarkdownDescription: "Manage EntKube configuration declaratively.",
		Attributes: map[string]schema.Attribute{
			"url": schema.StringAttribute{
				Optional:            true,
				MarkdownDescription: "Base URL of the EntKube instance. Falls back to `ENTKUBE_URL`.",
			},
			"token": schema.StringAttribute{
				Optional:  true,
				Sensitive: true,
				MarkdownDescription: "A scoped API token (`ekp_…`) with `config:read` and " +
					"`config:write`. Falls back to `ENTKUBE_TOKEN`, which is preferable — a token " +
					"written into a .tf file ends up in version control.",
			},
		},
	}
}

func (p *entkubeProvider) Configure(ctx context.Context, req provider.ConfigureRequest, resp *provider.ConfigureResponse) {
	var config providerModel
	resp.Diagnostics.Append(req.Config.Get(ctx, &config)...)
	if resp.Diagnostics.HasError() {
		return
	}

	// Environment first in the fallback order below, but config wins when both are set:
	// an explicit value in the file is a deliberate override.
	url := os.Getenv("ENTKUBE_URL")
	if !config.URL.IsNull() && config.URL.ValueString() != "" {
		url = config.URL.ValueString()
	}

	token := os.Getenv("ENTKUBE_TOKEN")
	if !config.Token.IsNull() && config.Token.ValueString() != "" {
		token = config.Token.ValueString()
	}

	if url == "" {
		resp.Diagnostics.AddError("EntKube URL is not set",
			"Set the provider's `url` attribute or the ENTKUBE_URL environment variable.")
	}
	if token == "" {
		resp.Diagnostics.AddError("EntKube API token is not set",
			"Set the provider's `token` attribute or the ENTKUBE_TOKEN environment variable. "+
				"Create a token in EntKube under the tenant's API tokens tab, granting "+
				"config:read and config:write.")
	}
	if resp.Diagnostics.HasError() {
		return
	}

	client := NewClient(url, token)
	resp.ResourceData = client
	resp.DataSourceData = client
}

func (p *entkubeProvider) Resources(_ context.Context) []func() resource.Resource {
	return []func() resource.Resource{NewCostRateResource}
}

func (p *entkubeProvider) DataSources(_ context.Context) []func() datasource.DataSource {
	return []func() datasource.DataSource{NewClustersDataSource}
}
