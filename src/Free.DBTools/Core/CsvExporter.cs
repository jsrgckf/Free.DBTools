using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;

namespace Free.DBTools.Core
{
    public static class CsvExporter
    {
        public static void Export(DataView view, string filePath)
        {
            if (view == null || view.Table == null)
            {
                throw new ArgumentException("没有可导出的数据。", "view");
            }

            var table = view.Table;
            using (var writer = new StreamWriter(filePath, false, new UTF8Encoding(true)))
            {
                var headers = new List<string>();
                foreach (DataColumn column in table.Columns)
                {
                    headers.Add(Escape(column.ColumnName));
                }
                writer.WriteLine(string.Join(",", headers.ToArray()));

                foreach (DataRowView rowView in view)
                {
                    var values = new List<string>(table.Columns.Count);
                    foreach (DataColumn column in table.Columns)
                    {
                        values.Add(Escape(FormatValue(rowView.Row[column])));
                    }
                    writer.WriteLine(string.Join(",", values.ToArray()));
                }
            }
        }

        public static void Export(DataTable table, string filePath)
        {
            if (table == null)
            {
                throw new ArgumentException("没有可导出的数据。", "table");
            }
            Export(table.DefaultView, filePath);
        }

        private static string FormatValue(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return string.Empty;
            }

            var dateTime = value as DateTime?;
            if (dateTime.HasValue)
            {
                return dateTime.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }

            var offset = value as DateTimeOffset?;
            if (offset.HasValue)
            {
                return offset.Value.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            }

            var bytes = value as byte[];
            if (bytes != null)
            {
                var length = Math.Min(bytes.Length, 2000);
                var hex = BitConverter.ToString(bytes, 0, length).Replace("-", string.Empty);
                return bytes.Length > length ? "0x" + hex + "..." : "0x" + hex;
            }

            var formattable = value as IFormattable;
            if (formattable != null)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        private static string Escape(string value)
        {
            value = value ?? string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }
    }
}
