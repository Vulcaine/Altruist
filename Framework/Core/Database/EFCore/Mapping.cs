using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Altruist.Database;

// public class MappingConfig<T> : IEntityTypeConfiguration<T> where T : class
// {
//     public void Configure(EntityTypeBuilder<T> builder)
//     {
//         // Handle Table Mapping
//         var tableAttribute = typeof(T).GetCustomAttribute<TableAttribute>();
//         if (tableAttribute != null)
//         {
//             builder.Metadata.SetAnnotation("Table", tableAttribute.Name);
//         }

//         // Handle Property Mapping
//         foreach (var prop in typeof(T).GetProperties())
//         {
//             var columnAttribute = prop.GetCustomAttribute<ColumnAttribute>();
//             if (columnAttribute != null)
//             {
//                 builder.Property(prop.Name).HasColumnName(columnAttribute.Name);  // ✅ Correct way to set column name
//             }

//             // Handle Primary Key
//             if (prop.GetCustomAttribute<PrimaryKeyAttribute>() != null)
//             {
//                 builder.HasKey(prop.Name);  // ✅ Correct way to set primary key
//             }
//         }
//     }
// }
