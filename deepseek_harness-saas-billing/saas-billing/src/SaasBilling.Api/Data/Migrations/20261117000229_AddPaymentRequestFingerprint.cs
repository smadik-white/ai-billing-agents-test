using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SaasBilling.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRequestFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                table: "payment_attempts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                table: "payment_attempts");
        }
    }
}
