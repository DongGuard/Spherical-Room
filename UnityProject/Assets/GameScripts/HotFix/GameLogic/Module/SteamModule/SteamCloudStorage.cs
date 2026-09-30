#if UNITY_STANDALONE_WIN
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Cysharp.Threading.Tasks;
using Steamworks;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// Steam云存储
    /// </summary>
    public class SteamCloudStorage : Singleton<SteamCloudStorage>, IUpdate
    {
        private const string saveName = "save.txt";

        public void OnUpdate()
        {
        }

        /// <summary>
        /// 读取云存储
        /// </summary>
        public string LoadCloudStorage()
        {
            if (SteamManager.Initialized)
            {
                if (!SteamRemoteStorage.FileExists(saveName))
                {
                    Debug.Log("文件读取失败！可能是文件不存在。");
                }
                else
                {
                    int size = SteamRemoteStorage.GetFileSize(saveName);
                    byte[] buffer = new byte[size];
                    SteamRemoteStorage.FileRead(saveName, buffer, size);
                    Debug.Log("文件读取成功！");
                    string content = FullDecompressData(buffer);
                    Debug.Log("读取云存档内容: " + content);
                    if (!string.IsNullOrEmpty(content))
                    {
                        Debug.Log("Steam云存储数据测试成功!");
                    }
                    else
                    {
                        Debug.Log("Steam云存储数据测试失败!");
                    }

                    return content;
                }
            }

            return null;
        }

        // 删除云存档文件
        public async UniTask DeleteCloudSave()
        {
            if (SteamRemoteStorage.FileExists(saveName))
            {
                bool result = SteamRemoteStorage.FileDelete(saveName);
                if (result)
                {
                    Debug.Log("云存档删除成功！");
                }
            }
        }

        /// <summary>
        /// 保存云存储
        /// </summary>
        public async UniTask SaveCloudStorage(string saveValue)
        {
            if (SteamManager.Initialized)
            {
                await DeleteCloudSave();
                await UniTask.Delay(1000);
                ulong totalQuota, availableQuota; // 改为 ulong 类型
                SteamRemoteStorage.GetQuota(out totalQuota, out availableQuota);
                if (availableQuota == 0)
                {
                    Debug.LogError("Steam 云存储空间已满！");
                    return;
                }

                // byte[] bytes = Encoding.UTF8.GetBytes(saveValue);
                byte[] compressedBytes = FullCompressData(saveValue);
                if ((ulong)compressedBytes.Length > availableQuota)
                {
                    Debug.LogError($"写入失败：数据大小{(ulong)compressedBytes.Length}字节超过可用配额{availableQuota}字节");
                    return;
                }

                if (!SteamUser.BLoggedOn())
                {
                    Debug.LogError("Steam未登录或处于离线状态，无法使用云存储");
                    return;
                }

// 检查当前游戏的云存储是否被用户手动关闭
                if (!SteamRemoteStorage.IsCloudEnabledForApp())
                {
                    Debug.LogError("用户已关闭当前游戏的云存储功能，请在Steam游戏属性中开启");
                    return;
                }

                bool success = SteamRemoteStorage.FileWrite(saveName, compressedBytes, compressedBytes.Length);
                if (success)
                {
                    Debug.Log("数据写入成功！");
                    SteamRemoteStorage.GetQuota(out totalQuota, out availableQuota);
                    if (availableQuota == 0)
                    {
                        Debug.LogError("Steam 云存储空间已满！");
                        return;
                    }

                    Debug.Log($"总配额: {totalQuota}, 可用配额: {availableQuota}");
                }
                else
                {
                    Debug.LogError("数据写入失败！");
                }
            }
        }


        private byte[] PreCompressData(string saveValue)
        {
            // 首先进行一次轻量的压缩
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal)) // 用较低的压缩级别进行快速压缩
                {
                    using (StreamWriter writer = new StreamWriter(gzip))
                    {
                        writer.Write(saveValue);
                    }
                }

                return ms.ToArray();
            }
        }


        private byte[] CompressData(byte[] preCompressedData)
        {
            // 对已经压缩的数据再次进行压缩
            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal)) // 使用更高的压缩级别进行二次压缩
                {
                    gzip.Write(preCompressedData, 0, preCompressedData.Length);
                }

                return ms.ToArray();
            }
        }

        private byte[] FullCompressData(string saveValue)
        {
            // 先压缩，再进行二次压缩
            byte[] preCompressedData = PreCompressData(saveValue);
            return CompressData(preCompressedData);
        }


        private byte[] DecompressData(byte[] compressedData)
        {
            using (MemoryStream ms = new MemoryStream(compressedData))
            {
                using (GZipStream gzip = new GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                {
                    using (MemoryStream outputStream = new MemoryStream())
                    {
                        gzip.CopyTo(outputStream); // 将解压后的数据写入 outputStream
                        return outputStream.ToArray(); // 返回解压后的字节数组
                    }
                }
            }
        }

        private string FullDecompressData(byte[] compressedData)
        {
            // 先解压二次压缩数据，得到字节数组
            byte[] preCompressedData = DecompressData(compressedData);

            // 再解压一次得到原始字节数据，并转换为字符串
            byte[] originalData = DecompressData(preCompressedData);
            return Encoding.UTF8.GetString(originalData); // 转换为字符串并返回
        }
    }
}
#endif