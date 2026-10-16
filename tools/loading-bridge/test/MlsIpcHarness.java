import java.lang.reflect.*;

/** Calls the real reference JAR's IPC producer without starting Minecraft or authenticating an account. */
public final class MlsIpcHarness {
    public static void main(String[] args) throws Exception {
        Class<?> screen = Class.forName("io.github.gaming32.modloadingscreen.ActualLoadingScreen");
        screen.getMethod("startLoadingScreen", boolean.class).invoke(null, false);
        Method begin = screen.getDeclaredMethod("beforeEntrypointType", String.class, String.class, int.class); begin.setAccessible(true);
        begin.invoke(null, "client", "ClientModInitializer", 4);
        screen.getMethod("beforeSingleEntrypoint", String.class, String.class, String.class, String.class)
            .invoke(null, "client", "ClientModInitializer", "test", "测试模组 🌙\u0000");
        Thread.sleep(800);
        screen.getMethod("createCustomProgressBar", String.class, String.class, int.class).invoke(null, "fixture", "游戏资源", 100);
        Method op = screen.getDeclaredMethod("customProgressBarOp", String[].class); op.setAccessible(true);
        op.invoke(null, (Object)new String[] { "fixture", "progress", "42" });
        Thread.sleep(800);
        Method close = screen.getDeclaredMethod("close"); close.setAccessible(true); close.invoke(null);
        Thread.sleep(300);
        System.out.println("MLS IPC harness completed");
    }
}
