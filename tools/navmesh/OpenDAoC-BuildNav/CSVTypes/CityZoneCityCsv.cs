using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CEM.Client.CSVTypes
{
  /// <summary>City from city.csv</summary>
  [StructLayout(LayoutKind.Sequential)]
  public class CityZoneCityCsv : ICsvParseable
  {
    // Count (skip)
    // Count times: index (1 based), nif file
    public int ID;
    public string NifName;

    public override string ToString()
    {
      return string.Format("[CityCSV] ID="+ID+" NifName="+NifName);
    }
  }
}
