using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddExternalRabbitMQClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminUsername",
                table: "RabbitMQClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialsPasswordKey",
                table: "RabbitMQClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialsSecretName",
                table: "RabbitMQClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialsUsernameKey",
                table: "RabbitMQClusters",
                type: "nvarchar(max)",
                nullable: true);

            // Every pre-existing row was provisioned through the cluster operator, so the
            // backfill must be true. The scaffolded default (the CLR default, false) would
            // silently reclassify them as external and disable their lifecycle operations.
            migrationBuilder.AddColumn<bool>(
                name: "IsOperatorManaged",
                table: "RabbitMQClusters",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceName",
                table: "RabbitMQClusters",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatefulSetName",
                table: "RabbitMQClusters",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminUsername",
                table: "RabbitMQClusters");

            migrationBuilder.DropColumn(
                name: "CredentialsPasswordKey",
                table: "RabbitMQClusters");

            migrationBuilder.DropColumn(
                name: "CredentialsSecretName",
                table: "RabbitMQClusters");

            migrationBuilder.DropColumn(
                name: "CredentialsUsernameKey",
                table: "RabbitMQClusters");

            migrationBuilder.DropColumn(
                name: "IsOperatorManaged",
                table: "RabbitMQClusters");

            migrationBuilder.DropColumn(
                name: "ServiceName",
                table: "RabbitMQClusters");

            migrationBuilder.DropColumn(
                name: "StatefulSetName",
                table: "RabbitMQClusters");
        }
    }
}
