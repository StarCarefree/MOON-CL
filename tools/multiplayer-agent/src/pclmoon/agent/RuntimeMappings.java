package pclmoon.agent;

import java.io.*;
import java.util.*;

/** A single official-obf -> intermediary/SRG edge, never a fuzzy member-name table. */
final class RuntimeMappings {
    final Map<String, String> classes = new HashMap<String, String>();
    final Map<String, String> members = new HashMap<String, String>();
    boolean srg;
    static String key(String owner, String name, String descriptor) { return owner + "\t" + name + "\t" + descriptor; }
    static void put(Map<String, String> map, String key, String value) throws IOException {
        if (value.isEmpty()) throw new IOException("Empty runtime mapping name");
        String previous = map.put(key, value);
        if (previous != null && !previous.equals(value)) throw new IOException("Conflicting runtime mapping: " + key);
    }
    void clazz(String source, String target) throws IOException { put(classes, source.replace('/', '.'), target.replace('/', '.')); }
    void member(String owner, String name, String desc, String target) throws IOException {
        put(members, key(owner.replace('/', '.'), name, desc), target);
    }
    String member(String owner, String name, String desc, boolean field) {
        String value = members.get(key(owner, name, desc));
        // TSRG fields have no descriptor: the official owner+obf field uniquely determines its type.
        return value != null || !field ? value : members.get(key(owner, name, ""));
    }
    static RuntimeMappings parse(BufferedReader reader) throws IOException {
        RuntimeMappings result = new RuntimeMappings();
        String first = reader.readLine();
        if (first == null) throw new IOException("Empty runtime mappings");
        if (first.startsWith("\ufeff")) first = first.substring(1);
        String[] header = first.split("\\s+");
        boolean tiny1 = header[0].equals("v1"), tiny2 = header[0].equals("tiny");
        boolean tsrg2 = header[0].equals("tsrg2");
        int target = 1;
        if (tiny1 || tiny2 || tsrg2) {
            int base = tiny2 ? 3 : 1;
            if (tiny2 && (header.length < 5 || !header[1].equals("2") || !header[2].equals("0")))
                throw new IOException("Only Tiny 2.0 supported");
            String source = tsrg2 ? "obf" : "official", destination = tsrg2 ? "srg" : "intermediary";
            if (header.length <= base + 1 || !header[base].equals(source))
                throw new IOException("Runtime mappings must use " + source + " as first namespace");
            target = -1;
            for (int i = base + 1; i < header.length; i++) if (header[i].equals(destination)) target = i - base;
            if (target < 0) throw new IOException("Missing target namespace " + destination);
        }
        result.srg = !tiny1 && !tiny2;
        String owner = null, line = tiny1 || tiny2 || tsrg2 ? reader.readLine() : first;
        while (line != null) {
            if (!line.trim().isEmpty() && !line.trim().startsWith("#")) {
                int depth = 0; while (depth < line.length() && line.charAt(depth) == '\t') depth++;
                String[] p = line.substring(depth).split(tiny1 || tiny2 ? "\t" : "\\s+", -1);
                if (tiny1) {
                    if (p[0].equals("CLASS") && p.length > 1 + target) result.clazz(p[1], p[1 + target]);
                    else if ((p[0].equals("FIELD") || p[0].equals("METHOD")) && p.length > 3 + target)
                        result.member(p[1], p[3], p[2], p[3 + target]);
                    else throw new IOException("Malformed Tiny v1 record");
                } else if (tiny2) {
                    if (depth == 0 && p[0].equals("c") && p.length > 1 + target) {
                        owner = p[1]; result.clazz(owner, p[1 + target]);
                    } else if (depth == 1 && (p[0].equals("f") || p[0].equals("m")) && owner != null && p.length > 2 + target) {
                        result.member(owner, p[2], p[1], p[2 + target]);
                    } else if (depth == 1 && p[0].equals("escaped-names")) {
                        throw new IOException("Tiny escaped-names is not supported");
                    } else if (depth == 0) throw new IOException("Malformed Tiny v2 class record");
                    // Property, comment, parameter and local-variable metadata are not members.
                } else {
                    if (depth == 0) {
                        if (p.length <= target) throw new IOException("Malformed TSRG class record");
                        owner = p[0]; result.clazz(owner, p[target]);
                    } else if (depth == 1 && owner != null) {
                        if (p.length < 2) throw new IOException("Malformed TSRG member record");
                        boolean method = p[1].startsWith("(");
                        int index = (method ? 1 : 0) + target;
                        if (p.length <= index) throw new IOException("Missing TSRG member target");
                        result.member(owner, p[0], method ? p[1] : "", p[index]);
                    }
                    // TSRG2 depth >= 2: static/parameter metadata, including extra id columns.
                }
            }
            line = reader.readLine();
        }
        if (result.classes.isEmpty()) throw new IOException("No runtime classes parsed");
        return result;
    }
}
