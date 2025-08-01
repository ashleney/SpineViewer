﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using Spine;
using SpineViewer.Extensions;
using SpineViewer.Models;
using SpineViewer.Resources;
using SpineViewer.Services;
using SpineViewer.Utils;
using SpineViewer.ViewModels.Exporters;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;

namespace SpineViewer.ViewModels.MainWindow
{
    public class SpineObjectListViewModel : ObservableObject
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 主窗口视图模型引用
        /// </summary>
        private readonly MainWindowViewModel _vmMain;

        /// <summary>
        /// 临时对象, 存储复制的模型参数
        /// </summary>
        private SpineObjectConfigModel? _copiedSpineObjectConfigModel = null;

        public SpineObjectListViewModel(MainWindowViewModel mainViewModel)
        {
            _vmMain = mainViewModel;
            _spineObjectModels = _vmMain.SpineObjects; // 缓存对象

            _frameExporterViewModel = new(_vmMain);
            _frameSequenceExporterViewModel = new(_vmMain);
            _ffmpegVideoExporterViewModel = new(_vmMain);
            _customFFmpegExporterViewModel = new(_vmMain);
        }

        /// <summary>
        /// 单帧导出 ViewModel
        /// </summary>
        public FrameExporterViewModel FrameExporterViewModel => _frameExporterViewModel;
        private readonly FrameExporterViewModel _frameExporterViewModel;

        /// <summary>
        /// 帧序列 ViewModel
        /// </summary>
        public FrameSequenceExporterViewModel FrameSequenceExporterViewModel => _frameSequenceExporterViewModel;
        private readonly FrameSequenceExporterViewModel _frameSequenceExporterViewModel;

        /// <summary>
        /// 动图/视频 ViewModel
        /// </summary>
        public FFmpegVideoExporterViewModel FFmpegVideoExporterViewModel => _ffmpegVideoExporterViewModel;
        private readonly FFmpegVideoExporterViewModel _ffmpegVideoExporterViewModel;

        /// <summary>
        /// 动图/视频 ViewModel
        /// </summary>
        public CustomFFmpegExporterViewModel CustomFFmpegExporterViewModel => _customFFmpegExporterViewModel;
        private readonly CustomFFmpegExporterViewModel _customFFmpegExporterViewModel;

        /// <summary>
        /// 已加载的 Spine 对象
        /// </summary>
        public ObservableCollectionWithLock<SpineObjectModel> SpineObjects => _spineObjectModels;
        private readonly ObservableCollectionWithLock<SpineObjectModel> _spineObjectModels;

        /// <summary>
        /// 列表视图选中项发生改变时同步内部模型列表状态
        /// </summary>
        public RelayCommand<IList?> Cmd_ListViewSelectionChanged => _cmd_ListViewSelectionChanged ??= new(ListViewSelectionChanged_Execute);
        private RelayCommand<IList?>? _cmd_ListViewSelectionChanged;

        private void ListViewSelectionChanged_Execute(IList? args)
        {
            if (args is null) return;
            lock (_spineObjectModels.Lock)
            {
                var selectedItems = args.Cast<SpineObjectModel>().ToArray();
                foreach (var it in _spineObjectModels.Except(selectedItems)) it.IsSelected = false;
                foreach (var it in selectedItems) it.IsSelected = true;
                _vmMain.SpineObjectTabViewModel.SelectedObjects = selectedItems;
            }
        }

        /// <summary>
        /// 弹窗添加单模型命令
        /// </summary>
        public RelayCommand Cmd_AddSpineObject => _cmd_AddSpineObject ??= new(AddSpineObject_Execute);
        private RelayCommand? _cmd_AddSpineObject;

        private void AddSpineObject_Execute()
        {
            MessagePopupService.Info("Not Implemented, please drag files into here or add them from clipboard :)");
        }

        /// <summary>
        /// 移除给定模型
        /// </summary>
        public RelayCommand<IList?> Cmd_RemoveSpineObject => _cmd_RemoveSpineObject ??= new(RemoveSpineObject_Execute, RemoveSpineObject_CanExecute);
        private RelayCommand<IList?>? _cmd_RemoveSpineObject;

        private void RemoveSpineObject_Execute(IList? args)
        {
            if (!RemoveSpineObject_CanExecute(args)) return;

            if (args.Count > 1)
            {
                if (!MessagePopupService.Quest(string.Format(AppResource.Str_RemoveItemsQuest, args.Count)))
                    return;
            }

            lock (_spineObjectModels.Lock)
            {
                // NOTE: 这里必须要浅拷贝一次, 不能直接对会被修改的绑定数据 args 进行 foreach 遍历
                foreach (var sp in args.Cast<SpineObjectModel>().ToArray())
                {
                    _spineObjectModels.Remove(sp);
                    sp.Dispose();
                }
            }
        }

        private bool RemoveSpineObject_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count <= 0) return false;
            return true;
        }

        /// <summary>
        /// 从剪贴板文件列表添加模型
        /// </summary>
        public RelayCommand Cmd_AddSpineObjectFromClipboard => _cmd_AddSpineObjectFromClipboard ??= new(AddSpineObjectFromClipboard_Execute);
        private RelayCommand? _cmd_AddSpineObjectFromClipboard;

        private void AddSpineObjectFromClipboard_Execute()
        {
            if (!Clipboard.ContainsFileDropList()) return;
            AddSpineObjectFromFileList(Clipboard.GetFileDropList().Cast<string>().ToArray());
        }

        /// <summary>
        /// 重新加载模型
        /// </summary>
        public RelayCommand<IList?> Cmd_ReloadSpineObject => _cmd_ReloadSpineObject ??= new(ReloadSpineObject_Execute, ReloadSpineObject_CanExecute);
        private RelayCommand<IList?>? _cmd_ReloadSpineObject;

        private void ReloadSpineObject_Execute(IList? args)
        {
            if (!ReloadSpineObject_CanExecute(args)) return;

            if (args.Count <= 1)
            {
                lock (_spineObjectModels.Lock)
                {
                    var sp = (SpineObjectModel)args[0];
                    var idx = _spineObjectModels.IndexOf(sp);
                    if (idx < 0) return;

                    try
                    {
                        var spNew = new SpineObjectModel(sp.SkelPath, sp.AtlasPath);
                        spNew.ObjectConfig = sp.ObjectConfig;
                        _spineObjectModels[idx] = spNew;
                        sp.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to reload spine {0}, {1}", sp.SkelPath, ex.Message);
                        _logger.Trace(ex.ToString());
                    }
                }
            }
            else
            {
                ProgressService.RunAsync((pr, ct) => ReloadSpineObjectsTask(
                    args.Cast<SpineObjectModel>().ToArray(), pr, ct),
                    AppResource.Str_ReloadSpineObjectsTitle
                );
            }
        }

        private bool ReloadSpineObject_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count <= 0) return false;
            return true;
        }

        private void ReloadSpineObjectsTask(SpineObjectModel[] spines, IProgressReporter reporter, CancellationToken ct)
        {
            int totalCount = spines.Length;
            int success = 0;
            int error = 0;

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;

            reporter.Total = totalCount;
            reporter.Done = 0;
            reporter.ProgressText = $"[0/{totalCount}]";
            for (int i = 0; i < totalCount; i++)
            {
                if (ct.IsCancellationRequested) break;

                var sp = spines[i];
                reporter.ProgressText = $"[{i}/{totalCount}] {sp.Name}";

                lock (_spineObjectModels.Lock)
                {
                    var idx = _spineObjectModels.IndexOf(sp);
                    if (idx >= 0)
                    {
                        try
                        {
                            var spNew = new SpineObjectModel(sp.SkelPath, sp.AtlasPath);
                            spNew.ObjectConfig = sp.ObjectConfig;
                            _spineObjectModels[idx] = spNew;
                            sp.Dispose();
                            success++;
                        }
                        catch (Exception ex)
                        {
                            error++;
                            _logger.Error("Failed to reload spine {0}, {1}", sp.SkelPath, ex.Message);
                            _logger.Trace(ex.ToString());
                        }
                    }
                }

                reporter.Done = i + 1;
                reporter.ProgressText = $"[{i + 1}/{totalCount}] {sp.Name}";
                _vmMain.ProgressValue = (i + 1f) / totalCount;
            }
            _vmMain.ProgressState = TaskbarItemProgressState.None;

            if (error > 0)
                _logger.Warn("Batch reload {0} successfully, {1} failed", success, error);
            else
                _logger.Info("{0} skel reloaded successfully", success);

            _logger.LogCurrentProcessMemoryUsage();
        }

        /// <summary>
        /// 模型上移一位
        /// </summary>
        public RelayCommand<IList?> Cmd_MoveUpSpineObject => _cmd_MoveUpSpineObject ??= new(MoveUpSpineObject_Execute, MoveUpSpineObject_CanExecute);
        private RelayCommand<IList?>? _cmd_MoveUpSpineObject;

        private void MoveUpSpineObject_Execute(IList? args)
        {
            if (!MoveUpSpineObject_CanExecute(args)) return;
            var sp = (SpineObjectModel)args[0];
            lock (_spineObjectModels.Lock)
            {
                var idx = _spineObjectModels.IndexOf(sp);
                if (idx <= 0) return;
                _spineObjectModels.Move(idx, idx - 1);
            }
        }

        private bool MoveUpSpineObject_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count != 1) return false;
            return true;
        }

        /// <summary>
        /// 模型下移一位
        /// </summary>
        public RelayCommand<IList?> Cmd_MoveDownSpineObject => _cmd_MoveDownSpineObject ??= new(MoveDownSpineObject_Execute, MoveDownSpineObject_CanExecute);
        private RelayCommand<IList?>? _cmd_MoveDownSpineObject;

        private void MoveDownSpineObject_Execute(IList? args)
        {
            if (!MoveDownSpineObject_CanExecute(args)) return;
            var sp = (SpineObjectModel)args[0];
            lock (_spineObjectModels.Lock)
            {
                var idx = _spineObjectModels.IndexOf(sp);
                if (idx < 0 || idx >= _spineObjectModels.Count - 1) return;
                _spineObjectModels.Move(idx, idx + 1);
            }
        }

        private bool MoveDownSpineObject_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count != 1) return false;
            return true;
        }

        /// <summary>
        /// 复制模型参数
        /// </summary>
        public RelayCommand<IList?> Cmd_CopySpineObjectConfig => _cmd_CopySpineObjectConfig ??= new(CopySpineObjectConfig_Execute, CopySpineObjectConfig_CanExecute);
        private RelayCommand<IList?>? _cmd_CopySpineObjectConfig;

        private void CopySpineObjectConfig_Execute(IList? args)
        {
            if (!CopySpineObjectConfig_CanExecute(args)) return;
            var sp = (SpineObjectModel)args[0];
            _copiedSpineObjectConfigModel = sp.ObjectConfig;
            _logger.Info("Copy config from model: {0}", sp.Name);
        }

        private bool CopySpineObjectConfig_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count != 1) return false;
            return true;
        }

        /// <summary>
        /// 应用复制的模型参数
        /// </summary>
        public RelayCommand<IList?> Cmd_ApplySpineObjectConfig => _cmd_ApplySpineObjectConfig ??= new(ApplySpineObjectConfig_Execute, ApplySpineObjectConfig_CanExecute);
        private RelayCommand<IList?>? _cmd_ApplySpineObjectConfig;

        private void ApplySpineObjectConfig_Execute(IList? args)
        {
            if (!ApplySpineObjectConfig_CanExecute(args)) return;
            foreach (SpineObjectModel sp in args)
            {
                sp.ObjectConfig = _copiedSpineObjectConfigModel;
                _logger.Info("Apply config to model: {0}", sp.Name);
            }
        }

        private bool ApplySpineObjectConfig_CanExecute(IList? args)
        {
            if (_copiedSpineObjectConfigModel is null) return false;
            if (args is null) return false;
            if (args.Count <= 0) return false;
            return true;
        }

        public RelayCommand<IList?> Cmd_ApplySpineObjectConfigFromFile => _cmd_ApplySpineObjectConfigFromFile ??= new(ApplySpineObjectConfigFromFile_Execute, ApplySpineObjectConfigFromFile_CanExecute);
        private RelayCommand<IList?>? _cmd_ApplySpineObjectConfigFromFile;

        private void ApplySpineObjectConfigFromFile_Execute(IList? args)
        {
            if (!ApplySpineObjectConfigFromFile_CanExecute(args)) return;
            if (!DialogService.ShowOpenJsonDialog(out var fileName)) return;
            if (JsonHelper.Deserialize<SpineObjectConfigModel>(fileName, out var config))
            {
                foreach (SpineObjectModel sp in args)
                {
                    sp.ObjectConfig = config;
                    _logger.Info("Apply config to model: {0}", sp.Name);
                }
            }
        }

        private bool ApplySpineObjectConfigFromFile_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count <= 0) return false;
            return true;
        }

        public RelayCommand<IList?> Cmd_SaveSpineObjectConfigToFile => _cmd_SaveSpineObjectConfigToFile ??= new(SaveSpineObjectConfigToFile_Execute, SaveSpineObjectConfigToFile_CanExecute);
        private RelayCommand<IList?>? _cmd_SaveSpineObjectConfigToFile;

        private void SaveSpineObjectConfigToFile_Execute(IList? args)
        {
            if (!SaveSpineObjectConfigToFile_CanExecute(args)) return;
            var sp = (SpineObjectModel)args[0];
            var config = sp.ObjectConfig;

            string fileName = $"{Path.ChangeExtension(Path.GetFileName(sp.SkelPath), ".jcfg")}";
            if (!DialogService.ShowSaveJsonDialog(ref fileName, sp.AssetsDir)) return;
            JsonHelper.Serialize(sp.ObjectConfig, fileName);
        }

        private bool SaveSpineObjectConfigToFile_CanExecute(IList? args)
        {
            if (args is null) return false;
            if (args.Count != 1) return false;
            return true;
        }

        /// <summary>
        /// 从路径列表添加对象
        /// </summary>
        /// <param name="paths">可以是文件和文件夹</param>
        public void AddSpineObjectFromFileList(IEnumerable<string> paths)
        {
            List<string> validPaths = [];
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    var lowerPath = path.ToLower();
                    if (SpineObject.PossibleSuffixMapping.Keys.Any(lowerPath.EndsWith))
                        validPaths.Add(path);
                }
                else if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                    {
                        var lowerPath = file.ToLower();
                        if (SpineObject.PossibleSuffixMapping.Keys.Any(lowerPath.EndsWith))
                            validPaths.Add(file);
                    }
                }
            }

            if (validPaths.Count > 1)
            {
                if (validPaths.Count > 100)
                {
                    if (!MessagePopupService.Quest(string.Format(AppResource.Str_TooManyItemsToAddQuest, validPaths.Count)))
                        return;
                }
                ProgressService.RunAsync((pr, ct) => AddSpineObjectsTask(
                    validPaths.ToArray(), pr, ct),
                    AppResource.Str_AddSpineObjectsTitle
                );
            }
            else if (validPaths.Count > 0)
            {
                AddSpineObject(validPaths[0]);
                _logger.LogCurrentProcessMemoryUsage();
            }
        }

        /// <summary>
        /// 用于后台添加模型的任务方法
        /// </summary>
        private void AddSpineObjectsTask(string[] paths, IProgressReporter reporter, CancellationToken ct)
        {
            int totalCount = paths.Length;
            int success = 0;
            int error = 0;

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;

            reporter.Total = totalCount;
            reporter.Done = 0;
            reporter.ProgressText = $"[0/{totalCount}]";
            for (int i = 0; i < totalCount; i++)
            {
                if (ct.IsCancellationRequested) break;

                var skelPath = paths[i];
                reporter.ProgressText = $"[{i}/{totalCount}] {skelPath}";

                if (AddSpineObject(skelPath))
                    success++;
                else
                    error++;

                reporter.Done = i + 1;
                reporter.ProgressText = $"[{i + 1}/{totalCount}] {skelPath}";
                _vmMain.ProgressValue = (i + 1f) / totalCount;
            }
            _vmMain.ProgressState = TaskbarItemProgressState.None;

            if (error > 0)
                _logger.Warn("Batch load {0} successfully, {1} failed", success, error);
            else
                _logger.Info("{0} skel loaded successfully", success);

            _logger.LogCurrentProcessMemoryUsage();
        }

        /// <summary>
        /// 安全地在末尾添加一个模型, 发生错误会输出日志
        /// </summary>
        /// <returns>是否添加成功</returns>
        private bool AddSpineObject(string skelPath, string? atlasPath = null)
        {
            try
            {
                var sp = new SpineObjectModel(skelPath, atlasPath);
                lock (_spineObjectModels.Lock) _spineObjectModels.Add(sp);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Trace(ex.ToString());
                _logger.Error("Failed to load: {0}, {1}", skelPath, ex.Message);
            }
            return false;
        }

        private bool AddSpineObject(SpineObjectWorkspaceConfigModel cfg)
        {
            try
            {
                var sp = new SpineObjectModel(cfg);
                lock (_spineObjectModels.Lock) _spineObjectModels.Add(sp);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Trace(ex.ToString());
                _logger.Error("Failed to load: {0}, {1}", cfg.SkelPath, ex.Message);
            }
            return false;
        }

        public List<SpineObjectWorkspaceConfigModel> LoadedSpineObjects
        {
            get
            {
                List<SpineObjectWorkspaceConfigModel> loadedSpineObjects = [];
                lock (_spineObjectModels.Lock)
                {
                    foreach (var sp in _spineObjectModels)
                    {
                        loadedSpineObjects.Add(sp.WorkspaceConfig);
                    }
                }
                return loadedSpineObjects;
            }
            set
            {
                AddSpineObjectFromWorkspaceList(value);
            }
        }

        private void AddSpineObjectFromWorkspaceList(List<SpineObjectWorkspaceConfigModel> models)
        {
            lock (_spineObjectModels.Lock)
            {
                var spines = _spineObjectModels.ToArray();
                _spineObjectModels.Clear();
                foreach (var sp in spines)
                {
                    sp.Dispose();
                }
            }

            if (models.Count > 1)
            {
                ProgressService.RunAsync((pr, ct) => AddSpineObjectFromWorkspaceListTask(
                    models, pr, ct),
                    AppResource.Str_AddSpineObjectsTitle
                );
            }
            else if (models.Count > 0)
            {
                AddSpineObject(models[0]);
                _logger.LogCurrentProcessMemoryUsage();
            }
        }

        private void AddSpineObjectFromWorkspaceListTask(List<SpineObjectWorkspaceConfigModel> models, IProgressReporter reporter, CancellationToken ct)
        {
            int totalCount = models.Count;
            int success = 0;
            int error = 0;

            _vmMain.ProgressState = TaskbarItemProgressState.Normal;
            _vmMain.ProgressValue = 0;

            reporter.Total = totalCount;
            reporter.Done = 0;
            reporter.ProgressText = $"[0/{totalCount}]";
            for (int i = 0; i < totalCount; i++)
            {
                if (ct.IsCancellationRequested) break;

                var cfg = models[i];
                reporter.ProgressText = $"[{i}/{totalCount}] {cfg}";

                if (AddSpineObject(cfg))
                    success++;
                else
                    error++;

                reporter.Done = i + 1;
                reporter.ProgressText = $"[{i + 1}/{totalCount}] {cfg}";
                _vmMain.ProgressValue = (i + 1f) / totalCount;
            }
            _vmMain.ProgressState = TaskbarItemProgressState.None;

            if (error > 0)
                _logger.Warn("Batch load {0} successfully, {1} failed", success, error);
            else
                _logger.Info("{0} skel loaded successfully", success);

            _logger.LogCurrentProcessMemoryUsage();

            // 从工作区加载需要同步一次时间轴
            lock (_spineObjectModels.Lock)
            {
                foreach (var sp in _spineObjectModels) 
                    sp.ResetAnimationsTime();
            }
        }
    }
}
