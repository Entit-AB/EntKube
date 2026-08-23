// Terraform provider for EntKube.
//
// Manages EntKube's own configuration declaratively — the price sheets and other
// platform settings a team would rather keep in version control than click into a
// UI. It talks to the same public /api/v1 surface as every other client and carries
// an ordinary scoped API token, so it can do exactly what the token permits.
package main

import (
	"context"
	"flag"
	"log"

	"github.com/Entit-AB/terraform-provider-entkube/internal/provider"
	"github.com/hashicorp/terraform-plugin-framework/providerserver"
)

func main() {
	var debug bool
	flag.BoolVar(&debug, "debug", false, "run with support for debuggers")
	flag.Parse()

	err := providerserver.Serve(context.Background(), provider.New, providerserver.ServeOpts{
		Address: "registry.terraform.io/entit-ab/entkube",
		Debug:   debug,
	})
	if err != nil {
		log.Fatal(err)
	}
}
