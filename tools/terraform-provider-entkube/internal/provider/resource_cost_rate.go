package provider

import (
	"context"
	"errors"

	"github.com/hashicorp/terraform-plugin-framework/path"
	"github.com/hashicorp/terraform-plugin-framework/resource"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/booldefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/planmodifier"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringdefault"
	"github.com/hashicorp/terraform-plugin-framework/resource/schema/stringplanmodifier"
	"github.com/hashicorp/terraform-plugin-framework/types"
)

type costRateResource struct{ client *Client }

func NewCostRateResource() resource.Resource { return &costRateResource{} }

type costRateModel struct {
	ClusterID              types.String  `tfsdk:"cluster_id"`
	CPUCoreHourCost        types.Float64 `tfsdk:"cpu_core_hour_cost"`
	MemoryGiBHourCost      types.Float64 `tfsdk:"memory_gib_hour_cost"`
	StorageGiBMonthCost    types.Float64 `tfsdk:"storage_gib_month_cost"`
	ClusterMonthlyOverhead types.Float64 `tfsdk:"cluster_monthly_overhead"`
	Currency               types.String  `tfsdk:"currency"`
	ChargeOnRequests       types.Bool    `tfsdk:"charge_on_requests"`
}

func (r *costRateResource) Metadata(_ context.Context, req resource.MetadataRequest, resp *resource.MetadataResponse) {
	resp.TypeName = req.ProviderTypeName + "_cost_rate"
}

func (r *costRateResource) Schema(_ context.Context, _ resource.SchemaRequest, resp *resource.SchemaResponse) {
	resp.Schema = schema.Schema{
		MarkdownDescription: "The price sheet for one cluster — what a unit of capacity costs, " +
			"so consumption can be turned into money and attributed to a customer.",
		Attributes: map[string]schema.Attribute{
			"cluster_id": schema.StringAttribute{
				Required:            true,
				MarkdownDescription: "Cluster this price sheet applies to.",
				PlanModifiers: []planmodifier.String{
					// A price sheet is keyed by cluster, so retargeting it is a different
					// resource entirely — replace rather than silently repricing another cluster.
					stringplanmodifier.RequiresReplace(),
				},
			},
			"cpu_core_hour_cost": schema.Float64Attribute{
				Required:            true,
				MarkdownDescription: "Cost of one CPU core for one hour.",
			},
			"memory_gib_hour_cost": schema.Float64Attribute{
				Required:            true,
				MarkdownDescription: "Cost of one GiB of memory for one hour.",
			},
			"storage_gib_month_cost": schema.Float64Attribute{
				Required:            true,
				MarkdownDescription: "Cost of one GiB of provisioned storage for a 730-hour month.",
			},
			"cluster_monthly_overhead": schema.Float64Attribute{
				Optional:            true,
				Computed:            true,
				MarkdownDescription: "Fixed monthly cost for the cluster itself, spread across consumers in proportion to compute.",
			},
			"currency": schema.StringAttribute{
				Optional:            true,
				Computed:            true,
				Default:             stringdefault.StaticString("USD"),
				MarkdownDescription: "ISO currency code. Display only; no conversion is performed.",
			},
			"charge_on_requests": schema.BoolAttribute{
				Optional: true,
				Computed: true,
				Default:  booldefault.StaticBool(true),
				MarkdownDescription: "Charge on requests rather than actual usage. Requests are what the " +
					"scheduler reserves and therefore what a customer denies to everyone else, which is " +
					"the defensible basis for a chargeback.",
			},
		},
	}
}

func (r *costRateResource) Configure(_ context.Context, req resource.ConfigureRequest, resp *resource.ConfigureResponse) {
	if req.ProviderData == nil {
		return
	}
	r.client = req.ProviderData.(*Client)
}

func (r *costRateResource) apply(ctx context.Context, plan costRateModel) (*CostRate, error) {
	return r.client.PutCostRate(ctx, plan.ClusterID.ValueString(), CostRate{
		CPUCoreHourCost:        plan.CPUCoreHourCost.ValueFloat64(),
		MemoryGiBHourCost:      plan.MemoryGiBHourCost.ValueFloat64(),
		StorageGiBMonthCost:    plan.StorageGiBMonthCost.ValueFloat64(),
		ClusterMonthlyOverhead: plan.ClusterMonthlyOverhead.ValueFloat64(),
		Currency:               plan.Currency.ValueString(),
		ChargeOnRequests:       plan.ChargeOnRequests.ValueBool(),
	})
}

// toModel maps the server's response back into state. Reading back what the server
// stored, rather than echoing the plan, is what makes a subsequent plan honest: if
// EntKube clamped or normalised a value, Terraform should show that as drift.
func toModel(clusterID string, rate *CostRate) costRateModel {
	return costRateModel{
		ClusterID:              types.StringValue(clusterID),
		CPUCoreHourCost:        types.Float64Value(rate.CPUCoreHourCost),
		MemoryGiBHourCost:      types.Float64Value(rate.MemoryGiBHourCost),
		StorageGiBMonthCost:    types.Float64Value(rate.StorageGiBMonthCost),
		ClusterMonthlyOverhead: types.Float64Value(rate.ClusterMonthlyOverhead),
		Currency:               types.StringValue(rate.Currency),
		ChargeOnRequests:       types.BoolValue(rate.ChargeOnRequests),
	}
}

func (r *costRateResource) Create(ctx context.Context, req resource.CreateRequest, resp *resource.CreateResponse) {
	var plan costRateModel
	resp.Diagnostics.Append(req.Plan.Get(ctx, &plan)...)
	if resp.Diagnostics.HasError() {
		return
	}

	saved, err := r.apply(ctx, plan)
	if err != nil {
		resp.Diagnostics.AddError("Could not create the price sheet", err.Error())
		return
	}

	resp.Diagnostics.Append(resp.State.Set(ctx, toModel(plan.ClusterID.ValueString(), saved))...)
}

func (r *costRateResource) Read(ctx context.Context, req resource.ReadRequest, resp *resource.ReadResponse) {
	var state costRateModel
	resp.Diagnostics.Append(req.State.Get(ctx, &state)...)
	if resp.Diagnostics.HasError() {
		return
	}

	rate, err := r.client.GetCostRate(ctx, state.ClusterID.ValueString())
	if errors.Is(err, ErrNotFound) {
		// Removed outside Terraform: drop it from state so the next plan re-creates it.
		// Only on an explicit not-found — doing this on a transient error would silently
		// discard a resource that still exists.
		resp.State.RemoveResource(ctx)
		return
	}
	if err != nil {
		resp.Diagnostics.AddError("Could not read the price sheet", err.Error())
		return
	}

	resp.Diagnostics.Append(resp.State.Set(ctx, toModel(state.ClusterID.ValueString(), rate))...)
}

func (r *costRateResource) Update(ctx context.Context, req resource.UpdateRequest, resp *resource.UpdateResponse) {
	var plan costRateModel
	resp.Diagnostics.Append(req.Plan.Get(ctx, &plan)...)
	if resp.Diagnostics.HasError() {
		return
	}

	saved, err := r.apply(ctx, plan)
	if err != nil {
		resp.Diagnostics.AddError("Could not update the price sheet", err.Error())
		return
	}

	resp.Diagnostics.Append(resp.State.Set(ctx, toModel(plan.ClusterID.ValueString(), saved))...)
}

func (r *costRateResource) Delete(ctx context.Context, req resource.DeleteRequest, resp *resource.DeleteResponse) {
	var state costRateModel
	resp.Diagnostics.Append(req.State.Get(ctx, &state)...)
	if resp.Diagnostics.HasError() {
		return
	}

	err := r.client.DeleteCostRate(ctx, state.ClusterID.ValueString())
	// Already gone is a successful destroy, not a failure — otherwise a half-finished
	// destroy can never be completed.
	if err != nil && !errors.Is(err, ErrNotFound) {
		resp.Diagnostics.AddError("Could not delete the price sheet", err.Error())
	}
}

func (r *costRateResource) ImportState(ctx context.Context, req resource.ImportStateRequest, resp *resource.ImportStateResponse) {
	// Importing by cluster id lets a team adopt price sheets that already exist rather
	// than having to destroy and re-create them under Terraform.
	resp.Diagnostics.Append(resp.State.SetAttribute(ctx, path.Root("cluster_id"), req.ID)...)
}
