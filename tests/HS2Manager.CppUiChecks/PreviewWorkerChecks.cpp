#include "Preview/NativePreviewWorker.h"
#include <atomic>
#include <iostream>

using namespace hs2;
using namespace hs2::ui;

int main()
{
    auto parent = fs::temp_directory_path();
    auto root = parent / ("hs2-preview-worker-" + NewId());
    try
    {
        {
            PreviewReadyDelivery delivery;
            auto definition = std::make_shared<CharacterDefinition>();
            std::weak_ptr<CharacterDefinition> released = definition;
            PreviewReady ready;
            ready.Definition = std::move(definition);
            if (!delivery.Publish(std::move(ready)))
                throw std::runtime_error("Ready delivery unexpectedly closed.");
            delivery.Close();
            if (!released.expired() || delivery.Take())
                throw std::runtime_error("Closed delivery retained a queued preview resource.");
            if (delivery.Publish({}))
                throw std::runtime_error("Closed delivery accepted a late preview callback.");
        }
        for (int index = 0; index < 100; ++index)
        {
            PreviewReadyDelivery delivery;
            std::jthread producer(
                [&]
                {
                    for (int item = 0; item < 100; ++item)
                        delivery.Publish({});
                });
            delivery.Close();
            producer.join();
            if (delivery.Take())
                throw std::runtime_error("Concurrent delivery survived close.");
        }
        std::atomic<int> callbacks{};
        PreviewCallbacks events;
        events.Ready = [&](auto)
        {
            ++callbacks;
        };
        events.Failure = [&](auto, auto)
        {
            ++callbacks;
        };
        CardRecord missing;
        missing.Path = Utf8(root / "missing.png");
        {
            NativePreviewWorker worker(root / "NoGame", root, root / "missing.tpk", events);
            for (int index = 0; index < 2000; ++index)
                worker.Visibility({index % 3, index % 2 == 0});
            worker.Quiesce();
            if (callbacks.load() != 0)
                throw std::runtime_error("Visibility-only commands attempted to load character resources.");
            for (int index = 0; index < 2000; ++index)
            {
                // 模拟面板绑定后的加载紧接着关闭；工作线程未必已经处理恢复通知。
                worker.Quiesce();
                worker.Load(missing);
                worker.Quiesce();
                worker.Unload();
            }
        }
        // 析构必须等待工作线程及回调结束，不能把任务留给进程退出来回收。
        auto completed = callbacks.load();
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
        if (callbacks.load() != completed)
            throw std::runtime_error("Preview callback ran after worker destruction.");
        if (!fs::equivalent(root.parent_path(), parent) || !root.filename().string().starts_with("hs2-preview-worker-"))
            throw std::runtime_error("Invalid test cleanup path.");
        fs::remove_all(root);
        std::cout
            << "PASS: 2000 immediate load/close transitions, joined worker lifetime and cancelled queued resources\n";
        return 0;
    }
    catch (std::exception const &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
