using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TEngine.Editor;
using TEngine.Localization;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Type = System.Type;

public class LubanEditor : EditorWindow
{
    private static List<String> fileName = new List<string>();
    private int selectedIndex = 0;
    private static string excelName;
    private static string fileExcelPath;
    public static string dynamicsRelativePath = "Assets/Editor/I2Localization/Localization_dynamics.csv";
    public static string StaticRelativePath = "Assets/Editor/I2Localization/Localization_static.csv";
    public static string MaterialsPath = "Assets/Editor/I2Localization/Localization_Materials.csv";
    public static string Localization = "Assets/Editor/I2Localization/Localization.csv";

    [MenuItem("TEngine/Luban/转表And验表")]
    private static void Init()
    {
        LubanEditor window = EditorWindow.GetWindow<LubanEditor>();
    }

    private void OnGUI()
    {
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.Label("Meter Transfer Tool  (配置表格前一定要跟着转表文档走配置一下!!)");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("(请选择一个表格打开)Please select the table you want to open：");
        GetExcelFileName();
        selectedIndex = EditorGUILayout.Popup(selectedIndex, fileName.ToArray());
        GUILayout.Space(20);
        if (GUILayout.Button("Open"))
        {
            OpenConfigFolder(fileName[selectedIndex]);
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(10);
        GUILayout.BeginHorizontal();
        GUILayout.Label("(请新建一个表格并打开)Please enter the name of the excel you want to create：");
        excelName = EditorGUILayout.TextField(excelName);
        if (GUILayout.Button("Create Excel And Open"))
        {
            if (!string.IsNullOrEmpty(excelName))
            {
                CreateExcelAndOpen();
            }
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(20);
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.Label("首先先进行转表(如果有错误请先修复错误)", EditorStyles.boldLabel);
        if (GUILayout.Button("One-click table transfer"))
        {
            BuildLubanExcel();
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(10);
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.Label("如果上面转表没有报错在点击多语言数据导入(必须先确保上面数据转换没有错误)", EditorStyles.boldLabel);
        if (GUILayout.Button("BuildLanguages"))
        {
            BuildLanguages();
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private static void OpenConfigFolder(String name)
    {
        OpenFolderHelper.Execute(Application.dataPath + @"/../../Configs/GameConfig/Datas" + $"/{name}");
    }

    private static void GetExcelFileName()
    {
        string directoryPath = Application.dataPath + @"/../../Configs/GameConfig/Datas";
        string[] files = Directory.GetFiles(directoryPath, "*.xlsx")
            .Concat(Directory.GetFiles(directoryPath, "*.csv"))
            .Where(file => !Path.GetFileName(file).StartsWith("~"))
            .ToArray();
        foreach (string file in files)
        {
            fileName.Add(Path.GetFileName(file));
        }
    }

    private static void BuildLubanExcel()
    {
        Application.OpenURL(
            Application.dataPath + @"/../../Configs/GameConfig/gen_code_bin_to_project_lazyload.bat");
    }

    private static void BuildLanguages()
    {
        OverwriteCSV();
        var source = LocalizationManager.GetEditorAsset().mSource;
        string absolutePath = "";
        List<LanguageData> mLanguages = new List<LanguageData>(source.mLanguages);
        // 读取 CSV
        var encoding = Encoding.UTF8;
        absolutePath = Path.GetFullPath(AssetDatabase.GetAssetPath(AssetDatabase.LoadAssetAtPath<TextAsset>(dynamicsRelativePath)));
        string csvContent = LocalizationReader.ReadCSVfile(absolutePath, encoding);

        // 调用 LanguageSourceData 的导入方法
        char separator = ','; // 或根据项目设置调整
        string error = source.Import_CSV(null, csvContent, eSpreadsheetUpdateMode.Replace, separator);
        source.mLanguages = mLanguages;
        mLanguages = new List<LanguageData>(source.mLanguages);
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError("CSV 导入失败: " + error);
        }
        else
        {
            Debug.Log("CSV 导入成功");
            // 读取 CSV
            absolutePath = Path.GetFullPath(AssetDatabase.GetAssetPath(AssetDatabase.LoadAssetAtPath<TextAsset>(StaticRelativePath)));
            string csvContentStatic = LocalizationReader.ReadCSVfile(absolutePath, encoding);

            // 调用 LanguageSourceData 的导入方法
            string errorStatic = source.Import_CSV(null, csvContentStatic, eSpreadsheetUpdateMode.Merge, separator);
            source.mLanguages = mLanguages;
            mLanguages = new List<LanguageData>(source.mLanguages);
            if (!string.IsNullOrEmpty(errorStatic))
            {
                Debug.LogError("CSV 导入失败: " + error);
            }
            else
            {
                absolutePath = Path.GetFullPath(AssetDatabase.GetAssetPath(AssetDatabase.LoadAssetAtPath<TextAsset>(MaterialsPath)));
                string csvContentMaterial = LocalizationReader.ReadCSVfile(absolutePath, encoding);

                // 调用 LanguageSourceData 的导入方法
                string errorMaterial = source.Import_CSV(null, csvContentMaterial, eSpreadsheetUpdateMode.Merge, separator);
                source.mLanguages = mLanguages;
                mLanguages = new List<LanguageData>(source.mLanguages);
                if (!string.IsNullOrEmpty(errorMaterial))
                {
                    Debug.LogError("CSV 导入失败: " + error);
                }
                else
                {
                    string CSVstring = source.Export_CSV(null, ',');
                    File.WriteAllText(Localization, CSVstring, encoding);
                    source.mLanguages = mLanguages;
                    EditorUtility.SetDirty(LocalizationManager.GetEditorAsset());
                    AssetDatabase.Refresh();
                    Debug.Log("导出成功!");
                }
            }
        }
    }

    private static void CreateExcelAndOpen()
    {
        fileExcelPath ??= Application.dataPath + "/../../Configs/GameConfig/Datas/~模板.xlsx";
        String path = Application.dataPath + @"/../../Configs/GameConfig/Datas/" + $"{excelName}" + ".xlsx";
        if (File.Exists(path))
        {
            Debug.LogError("The file already exists: " + $"{excelName}" + ".xlsx");
            return;
        }

        File.Copy(fileExcelPath, path, true);
        OpenConfigFolder($"{excelName}" + ".xlsx");
        excelName = "";
    }

    private static void OverwriteCSV()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;

        string targetFullPath = Path.Combine(projectRoot, dynamicsRelativePath);
        string sourceFullPath = Path.Combine(Directory.GetParent(Application.dataPath).Parent.FullName, "Configs/GameConfig/Datas/Localization.csv");

        if (!File.Exists(sourceFullPath))
        {
            Debug.LogError("源文件不存在: " + sourceFullPath);
            return;
        }

        try
        {
            // 读取源 CSV
            string[] sourceLines = File.ReadAllLines(sourceFullPath);

            // 从第三行开始覆盖（如果你想保留前三行，也可以单独处理）
            int startRow = 3;

            // 清空目标文件，先写入前三行（如果需要保留前三行，可保留，否则从 startRow 开始写）
            string[] newTargetLines = new string[sourceLines.Length - startRow];
            for (int i = startRow; i < sourceLines.Length; i++)
            {
                string[] sourceCols = sourceLines[i].Split(',');

                // 跳过源 CSV 第一列
                string[] sourceData = new string[sourceCols.Length - 1];
                Array.Copy(sourceCols, 1, sourceData, 0, sourceCols.Length - 1);

                // 生成新行
                newTargetLines[i - startRow] = string.Join(",", sourceData);
            }

            // 覆盖写入目标文件
            File.WriteAllLines(targetFullPath, newTargetLines);
            Debug.Log("CSV文件已成功覆盖（已清空原内容）: " + targetFullPath);

#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
        catch (Exception e)
        {
            Debug.LogError("覆盖CSV失败: " + e.Message);
        }
    }
}