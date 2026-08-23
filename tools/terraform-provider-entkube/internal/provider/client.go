package provider

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Client is a thin wrapper over EntKube's public API.
type Client struct {
	baseURL string
	token   string
	http    *http.Client
}

func NewClient(baseURL, token string) *Client {
	return &Client{
		baseURL: strings.TrimRight(baseURL, "/"),
		token:   token,
		http:    &http.Client{Timeout: 60 * time.Second},
	}
}

// ErrNotFound lets a resource tell "this is gone, plan a re-create" apart from a real
// failure. Terraform removes a resource from state on not-found; treating a transient
// error the same way would silently drop a resource that still exists.
var ErrNotFound = fmt.Errorf("not found")

func (c *Client) do(ctx context.Context, method, path string, body any, out any) error {
	var reader io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return err
		}
		reader = bytes.NewReader(encoded)
	}

	req, err := http.NewRequestWithContext(ctx, method, c.baseURL+path, reader)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+c.token)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	resp, err := c.http.Do(req)
	if err != nil {
		return fmt.Errorf("could not reach EntKube: %w", err)
	}
	defer resp.Body.Close()

	payload, _ := io.ReadAll(resp.Body)

	switch {
	case resp.StatusCode == http.StatusNotFound:
		return ErrNotFound
	case resp.StatusCode == http.StatusUnauthorized:
		return fmt.Errorf("the API token is missing, invalid, expired or revoked")
	case resp.StatusCode == http.StatusForbidden:
		// Lead with the server's message: it names the scope that was actually missing.
		// A hard-coded hint here was wrong whenever a different scope was at fault — it
		// told an operator to grant config:write when the data source needed fleet:read.
		return fmt.Errorf("the API token lacks a required scope: %s",
			scopeDetail(payload))
	case resp.StatusCode >= 400:
		return fmt.Errorf("EntKube returned HTTP %d: %s", resp.StatusCode, strings.TrimSpace(string(payload)))
	}

	if out != nil && len(payload) > 0 {
		if err := json.Unmarshal(payload, out); err != nil {
			return fmt.Errorf("could not parse the EntKube response: %w", err)
		}
	}
	return nil
}

// CostRate is one cluster's price sheet.
type CostRate struct {
	ClusterID               string  `json:"clusterId"`
	CPUCoreHourCost         float64 `json:"cpuCoreHourCost"`
	MemoryGiBHourCost       float64 `json:"memoryGiBHourCost"`
	StorageGiBMonthCost     float64 `json:"storageGiBMonthCost"`
	ClusterMonthlyOverhead  float64 `json:"clusterMonthlyOverhead"`
	LoadBalancerMonthlyCost float64 `json:"loadBalancerMonthlyCost"`
	PublicIpMonthlyCost     float64 `json:"publicIpMonthlyCost"`
	Currency                string  `json:"currency"`
	ChargeOnRequests        bool    `json:"chargeOnRequests"`
}

func (c *Client) GetCostRate(ctx context.Context, clusterID string) (*CostRate, error) {
	var rate CostRate
	if err := c.do(ctx, http.MethodGet, "/api/v1/cost-rates/"+clusterID, nil, &rate); err != nil {
		return nil, err
	}
	return &rate, nil
}

func (c *Client) PutCostRate(ctx context.Context, clusterID string, rate CostRate) (*CostRate, error) {
	var saved CostRate
	if err := c.do(ctx, http.MethodPut, "/api/v1/cost-rates/"+clusterID, rate, &saved); err != nil {
		return nil, err
	}
	return &saved, nil
}

func (c *Client) DeleteCostRate(ctx context.Context, clusterID string) error {
	return c.do(ctx, http.MethodDelete, "/api/v1/cost-rates/"+clusterID, nil, nil)
}

// Cluster is a registered Kubernetes cluster, for the data source.
type Cluster struct {
	ID             string `json:"id"`
	Name           string `json:"name"`
	APIServerURL   string `json:"apiServerUrl"`
	Environment    string `json:"environment"`
	ComponentCount int64  `json:"componentCount"`
}

func (c *Client) ListClusters(ctx context.Context) ([]Cluster, error) {
	var clusters []Cluster
	if err := c.do(ctx, http.MethodGet, "/api/v1/clusters", nil, &clusters); err != nil {
		return nil, err
	}
	return clusters, nil
}

// scopeDetail pulls the "detail" out of a ProblemDetails body so the operator sees the
// sentence naming the scope, not a wall of JSON.
func scopeDetail(payload []byte) string {
	var problem struct {
		Detail string `json:"detail"`
	}
	if err := json.Unmarshal(payload, &problem); err == nil && problem.Detail != "" {
		return problem.Detail
	}
	return strings.TrimSpace(string(payload))
}
