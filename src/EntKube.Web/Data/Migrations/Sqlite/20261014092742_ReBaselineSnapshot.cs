using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntKube.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ReBaselineSnapshot : Migration
    {
        /// <summary>
        /// <b>Deliberately empty.</b> This migration exists for its snapshot, not for its body.
        ///
        /// <para>Two earlier migrations — <c>AddMailDnsRecords</c> and
        /// <c>AddSupportRosterAndDeliveryReport</c> — were written by hand without updating
        /// <c>ModelSnapshot</c>. The databases are correct: both migrations are visible to EF and
        /// create everything the model describes, on all three providers. What was stale was EF's
        /// own baseline, and the consequence falls on the next developer rather than on any
        /// installation: <c>migrations add</c> diffs the model against the snapshot, so the next
        /// scaffolded migration re-created a table, five columns, a foreign key and two indexes
        /// that already exist — a migration that fails on every database it reaches.</para>
        ///
        /// <para>Scaffolding this and emptying it is what moves the baseline forward: the Designer
        /// file beside it and the top-level snapshot now describe the model as it stands, so the
        /// next real migration contains only what is genuinely new. The body is empty because every
        /// object in the diff was verified, per provider, to be created by a migration EF can
        /// see — so applying it would have been an error and recording it is the whole point.</para>
        ///
        /// <para><c>SqliteMigrationPathTests</c> pins the property this restores: no pending model
        /// changes, which is the check that would have caught it at the time.</para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
