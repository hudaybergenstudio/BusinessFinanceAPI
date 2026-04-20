using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessFinanceAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InterestRate",
                table: "Transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoanYears",
                table: "Transactions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterestRate",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "LoanYears",
                table: "Transactions");
        }
    }
}
