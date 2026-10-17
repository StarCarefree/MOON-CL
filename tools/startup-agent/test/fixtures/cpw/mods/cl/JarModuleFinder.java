package cpw.mods.cl;

import java.io.IOException;
import java.io.InputStream;
import java.lang.module.ModuleReader;
import java.net.URI;
import java.util.Optional;
import java.util.stream.Stream;

/** Independent close-boundary fixture. */
public final class JarModuleFinder {
    public static class JarModuleReader implements ModuleReader {
        public final ModuleReader delegate;
        public boolean failClose;
        public JarModuleReader(ModuleReader delegate) { this.delegate = delegate; }
        @Override public Optional<URI> find(String name) throws IOException { return delegate.find(name); }
        @Override public Optional<InputStream> open(String name) throws IOException { return delegate.open(name); }
        @Override public Stream<String> list() throws IOException { return delegate.list(); }
        @Override public void close() throws IOException {
            delegate.close();
            if (failClose) throw new IOException("fixture close failure");
        }
    }
}
