using System;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 云同步协调器的启动器替身。
    ///
    /// 启动器为了共用项目文件读写逻辑，会以源码链接方式编译 PublishPlanStore 与
    /// ProjectSyncProjectionStore；这两个类原本会通知 <c>CloudSyncCoordinator</c> 去同步，
    /// 而真正的协调器在主插件里、依赖 AutoCAD，不能被启动器编译。
    ///
    /// 所以这里给出同名同命名空间的替身：这些调用在启动器里**就是空操作**。
    /// 这不影响正确性——启动器执行项目管理时要求 AutoCAD 已关闭，
    /// 下一次进入 CAD 时同步引擎会自然地把改动同步出去。
    /// </summary>
    public static class CloudSyncCoordinator
    {
        public static void RequestSynchronization(bool immediate)
        {
        }

        public static void QueueReload(bool synchronizeAfterReload)
        {
        }
    }
}
