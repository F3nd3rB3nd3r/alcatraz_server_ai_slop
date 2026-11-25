using Alcatraz.DTO.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using System;
using System.Linq;


#nullable disable

namespace Alcatraz.Context.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePasswordStorage : Migration
    {
        MainDbContext _dbContext;
		public UpdatePasswordStorage(MainDbContext context)
		{
            _dbContext = context;
		}

		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
        {
			// Later migrations add properties in the current User model. Select
			// only columns which already exist at this migration's schema version.
			var passwords = _dbContext.Users.Select(user => new { user.Id, user.Password }).ToArray();
			foreach (var previous in passwords)
            {
				var user = new Entities.User
				{
					Id = previous.Id,
					Password = SecurePasswordHasher.Hash($"{previous.Id}-{previous.Password}")
				};
				_dbContext.Attach(user).Property(x => x.Password).IsModified = true;
			}
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

}
