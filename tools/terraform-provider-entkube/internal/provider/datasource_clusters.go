package provider

import (
	"context"

	"github.com/hashicorp/terraform-plugin-framework/datasource"
	"github.com/hashicorp/terraform-plugin-framework/datasource/schema"
	"github.com/hashicorp/terraform-plugin-framework/types"
)

type clustersDataSource struct{ client *Client }

func NewClustersDataSource() datasource.DataSource { return &clustersDataSource{} }

type clusterModel struct {
	ID             types.String `tfsdk:"id"`
	Name           types.String `tfsdk:"name"`
	APIServerURL   types.String `tfsdk:"api_server_url"`
	Environment    types.String `tfsdk:"environment"`
	ComponentCount types.Int64  `tfsdk:"component_count"`
}

type clustersModel struct {
	Clusters []clusterModel `tfsdk:"clusters"`
}

func (d *clustersDataSource) Metadata(_ context.Context, req datasource.MetadataRequest, resp *datasource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_clusters"
}

func (d *clustersDataSource) Schema(_ context.Context, _ datasource.SchemaRequest, resp *datasource.SchemaResponse) {
	resp.Schema = schema.Schema{
		MarkdownDescription: "Clusters registered in the tenant this token belongs to. " +
			"Lets a configuration attach price sheets by cluster name instead of hard-coding ids.",
		Attributes: map[string]schema.Attribute{
			"clusters": schema.ListNestedAttribute{
				Computed: true,
				NestedObject: schema.NestedAttributeObject{
					Attributes: map[string]schema.Attribute{
						"id":              schema.StringAttribute{Computed: true},
						"name":            schema.StringAttribute{Computed: true},
						"api_server_url":  schema.StringAttribute{Computed: true},
						"environment":     schema.StringAttribute{Computed: true},
						"component_count": schema.Int64Attribute{Computed: true},
					},
				},
			},
		},
	}
}

func (d *clustersDataSource) Configure(_ context.Context, req datasource.ConfigureRequest, resp *datasource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}
	d.client = req.ProviderData.(*Client)
}

func (d *clustersDataSource) Read(ctx context.Context, _ datasource.ReadRequest, resp *datasource.ReadResponse) {
	clusters, err := d.client.ListClusters(ctx)
	if err != nil {
		resp.Diagnostics.AddError("Could not list clusters", err.Error())
		return
	}

	state := clustersModel{Clusters: make([]clusterModel, 0, len(clusters))}
	for _, c := range clusters {
		state.Clusters = append(state.Clusters, clusterModel{
			ID:             types.StringValue(c.ID),
			Name:           types.StringValue(c.Name),
			APIServerURL:   types.StringValue(c.APIServerURL),
			Environment:    types.StringValue(c.Environment),
			ComponentCount: types.Int64Value(c.ComponentCount),
		})
	}

	resp.Diagnostics.Append(resp.State.Set(ctx, state)...)
}
