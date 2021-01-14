using CommandLine;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Bokio.MigrationSquasher
{
    class Program
    {
        public class Options
        {
            [Option('t', "target", Required = true, HelpText = "Last migration to include, the filename")]
            public string Target { get; set; }

            [Option('n', "newname", Required = true, HelpText = "The name of the new migration, the filename without the .cs postfix. Should be timestamp_name. The timestamp should match the target.")]
            public string NewName { get; set; }
        }

        const string MigrationNamePlaceholder = "$$MIGRATIONNAME$$";
        const string MigrationNameWithTimeStampPlaceholder = "$$MIGRATIONNAMEWITHTIMESTAMP$$";
        const string MigrationUpContentPlaceholder = "$$UP_CONTENT$$";
        private const string MigrationTimeStampFormat = "yyyyMMddHHmmss";

        static void Main(string[] args)
        {
            Console.WriteLine($"args:" + string.Join("\n", args));
            Parser.Default.ParseArguments<Options>(args)
            .WithNotParsed(errors => Console.WriteLine(string.Join(Environment.NewLine, errors)))
            .WithParsed(options =>
            {
                var doesExist = File.Exists(options.Target);
                if (!doesExist)
                {
                    Console.WriteLine("Shutting down: Could not find file " + options.Target);
                    return;
                }

                var targetFileName = Path.GetFileName(options.Target);
                var targetMigrationName = targetFileName.Replace(".cs", "");
                var newFileName = options.NewName + ".cs";
                var dir = Path.GetDirectoryName(options.Target);

                if(targetFileName.Substring(0, 14) != newFileName.Substring(0, 14))
                {
                    Console.WriteLine("Shutting down: The timestamp of the target and the new migration doesn't match");
                }

                var newTimeStamp = options.NewName.Substring(0, 14);
                var timestamp = DateTime.ParseExact(newTimeStamp, MigrationTimeStampFormat, null);
                var oneSecEarlier = timestamp.AddSeconds(-1);
                var prepMigrationName = options.NewName.Replace(newTimeStamp, oneSecEarlier.ToString(MigrationTimeStampFormat)) + "_prep";

                var migrationTemplate = File.ReadAllText("migrationtemplate.cs.template");

                var targetSnapshotTemplate = File.ReadAllText(Path.Combine(dir, targetMigrationName + ".Designer.cs"));
                
                // Write snapshot for new migration
                var newSnapshot = targetSnapshotTemplate
                .Replace($"partial class {targetMigrationName.Substring(15)}", $"partial class {options.NewName.Substring(15)}")
                .Replace($"[Migration(\"{targetMigrationName}\")]", $"[Migration(\"{options.NewName}\")]");

                File.WriteAllText(Path.Combine(dir, options.NewName + ".Designer.cs"), newSnapshot);

                // Write new migration file

                var migrationNames = Directory.GetFiles(dir)
                    .Where(f => !f.EndsWith(".Designer.cs", StringComparison.InvariantCultureIgnoreCase))
                    .ToList();

                var earlierMigrationNames = migrationNames.Where(f => Path.GetFileName(f).CompareTo(targetFileName) <= 0)
                .OrderBy(f => Path.GetFileName(f))
                .ToList(); // String comparison should be fine because it's using an ISO timestamp which is comparable

                var migrationContent = new StringBuilder();
                foreach (var migration in earlierMigrationNames)
                {
                    var migrationLines = File.ReadAllLines(Path.Combine(dir, migration)).ToList();
                    var upLine = migrationLines.Single(l => l.Contains("protected override void Up(MigrationBuilder migrationBuilder)"));
                    var downLine = migrationLines.Single(l => l.Contains("protected override void Down(MigrationBuilder migrationBuilder)"));

                    var upIndex = migrationLines.ToList().IndexOf(upLine);
                    var downIndex = migrationLines.ToList().IndexOf(downLine);

                    var upMigrationClosingLine = downIndex - 2; // Iffy, should iterate backwards instead
                    
                    migrationContent.AppendLine();
                    migrationContent.AppendLine("\t\t\t// From migration " + migration);
                    for (int lineIndex = upIndex + 2; lineIndex < upMigrationClosingLine; lineIndex++)
                    {
                        migrationContent.AppendLine(migrationLines[lineIndex]);
                    }
                }

                var newMigrationContent = migrationTemplate.Replace(MigrationNamePlaceholder, options.NewName.Substring(15));
                newMigrationContent = newMigrationContent.Replace(MigrationUpContentPlaceholder, migrationContent.ToString());

                File.WriteAllText(Path.Combine(dir, options.NewName + ".cs"), newMigrationContent);

                // Write the preparation migration. Must be written after the real migration to make sure the real migratin doesn't include this.
                var prepTemplate = migrationTemplate.Replace(MigrationNamePlaceholder, prepMigrationName.Substring(15));

                var prepSql = @$"migrationBuilder.Sql(@""
IF EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'{targetMigrationName}')
BEGIN    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'{options.NewName}', N'2.2.4-servicing-10062')
END;
GO"");";
                prepTemplate = prepTemplate.Replace(MigrationUpContentPlaceholder, prepSql);

                // Write prepation migration (Inserts migration in the DB)
                File.WriteAllText(Path.Combine(dir, prepMigrationName + ".cs"), prepTemplate);

                var prepSnapshot = File.ReadAllText("emptysnapshottemplate.cs.template")
                .Replace(MigrationNamePlaceholder, prepMigrationName.Substring(15))
                .Replace(MigrationNameWithTimeStampPlaceholder, prepMigrationName);

                File.WriteAllText(Path.Combine(dir, prepMigrationName + ".Designer.cs"), prepSnapshot);

                foreach (var oldMigration in earlierMigrationNames)
                {
                    File.Delete(oldMigration);
                    File.Delete(oldMigration.Replace(".cs", ".Designer.cs"));
                }

            });
        }
    }
}
