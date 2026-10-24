"""Isolated adapter contract tests. Never runs or edits an installed Minecraft instance."""
import os
from pathlib import Path
import subprocess
import zipfile

SOURCES = {
'com/mojang/blaze3d/textures/GpuTexture.java': '''package com.mojang.blaze3d.textures; public class GpuTexture {}''',
'net/minecraft/class_10868.java': '''package net.minecraft; public class class_10868 extends com.mojang.blaze3d.textures.GpuTexture { private static int next=1; private int id=next++; public int method_68427(){return id;} }''',
'net/minecraft/class_276.java': '''package net.minecraft;
public class class_276 { public int field_1482,field_1481; public final boolean field_1478; public static int created,deleted;
private final class_10868 color=new class_10868(),depth=new class_10868();
public class_276(int w,int h,boolean d){field_1482=w;field_1481=h;field_1478=d;created++;}
public com.mojang.blaze3d.textures.GpuTexture method_30277(){return color;}
public com.mojang.blaze3d.textures.GpuTexture method_30278(){return depth;}
public void method_1238(){deleted++;} }''',
'net/minecraft/class_6367.java': '''package net.minecraft; public class class_6367 extends class_276 { public class_6367(String label,int w,int h,boolean d){super(w,h,d);} }''',
'net/minecraft/class_1041.java': '''package net.minecraft; public class class_1041 { public int field_5181=1920,field_5196=1080; public int method_4489(){return field_5181;} public int method_4506(){return field_5196;} public int method_4480(){return 960;} public int method_4507(){return 540;} }''',
'net/minecraft/class_310.java': '''package net.minecraft; public class class_310 { public class_276 field_1689=new class_6367("native",1920,1080,true); public final class_1041 window=new class_1041(); public class_276 method_1522(){return field_1689;} public class_1041 method_22683(){return window;} }''',
'com/mojang/blaze3d/systems/CommandEncoder.java': '''package com.mojang.blaze3d.systems; import com.mojang.blaze3d.textures.GpuTexture; public interface CommandEncoder { void clearColorAndDepthTextures(GpuTexture c,int v,GpuTexture d,double z); }''',
'com/mojang/blaze3d/systems/GpuDevice.java': '''package com.mojang.blaze3d.systems; public interface GpuDevice { CommandEncoder createCommandEncoder(); }''',
'com/mojang/blaze3d/systems/RenderSystem.java': '''package com.mojang.blaze3d.systems; public class RenderSystem { public static int clears; private static final GpuDevice DEVICE=()->(c,v,d,z)->{clears++;}; public static GpuDevice getDevice(){return DEVICE;} }''',
'net/irisshaders/iris/Iris.java': '''package net.irisshaders.iris; public class Iris { public static boolean shaders; public static java.util.Optional<Object> getCurrentPack(){return shaders?java.util.Optional.of(new Object()):java.util.Optional.empty();} }''',
'net/minecraft/class_761.java': '''package net.minecraft; public class class_761 { public void method_22710(class_9922 a,class_9779 b,boolean c,class_4184 d,org.joml.Matrix4f e,org.joml.Matrix4f f,org.joml.Matrix4f g,com.mojang.blaze3d.buffers.GpuBufferSlice h,org.joml.Vector4f i,boolean j){} }''',
'net/minecraft/class_757.java': '''package net.minecraft; public class class_757 {
public final class_310 client=new class_310(); public int calls,seenWidth,seenHeight,seenInput,otherThreadWidth; public class_276 seenTarget; public RuntimeException failure;
public class_310 method_35772(){return client;}
public void method_3188(class_9779 counter){calls++;seenTarget=client.method_1522();seenWidth=client.window.method_4489();seenHeight=client.window.method_4506();seenInput=client.window.method_4480();
if(new class_1041().method_4489()!=1920)throw new AssertionError("unrelated window must stay native");
Thread t=new Thread(()->otherThreadWidth=client.window.method_4489());t.start();try{t.join();}catch(InterruptedException e){throw new RuntimeException(e);}
new class_761().method_22710(null,counter,false,null,null,null,null,null,null,false);if(failure!=null)throw failure;}
}''',
'pclmoon/framegen/test/SuperResolutionHarness.java': '''package pclmoon.framegen.test;
import net.minecraft.*;import pclmoon.framegen.bridge.*;import pclmoon.framegen.SuperResolutionTransformer;
import java.io.*;import org.objectweb.asm.*;
public class SuperResolutionHarness {
static void check(boolean ok,String why){if(!ok)throw new AssertionError(why);}
static int number(String key){return Integer.parseInt(System.getProperty(key,"0"));}
static byte[] bytes(String name)throws Exception{try(InputStream in=SuperResolutionHarness.class.getResourceAsStream("/"+name+".class")){ByteArrayOutputStream out=new ByteArrayOutputStream();byte[] b=new byte[4096];for(int n;(n=in.read(b))!=-1;)out.write(b,0,n);return out.toByteArray();}}
public static void main(String[] args)throws Throwable {
NativeUpscaleHooks.configure(new java.io.File(args[0]).toURI().toString());
class_757 world=new class_757();class_310 game=world.client;class_9779 counter=new class_9779();
if(args.length>1){UpscaleSettings.setEnabled(true);world.method_3188(counter);check(!UpscaleSettings.active()&&world.seenWidth==1920&&world.seenTarget==game.field_1689&&number("sr.construct")==0,"incomplete adapter must remain native");System.out.println("PASS unsupported SR getter: native fallback, no GPU allocation");return;}
UpscaleSettings.apply(false,67,20);world.method_3188(counter);check(world.seenTarget==game.field_1689&&world.seenWidth==1920,"default native");check(number("sr.construct")==0,"disabled does not load GPU");
UpscaleSettings.apply(true,100,0);world.method_3188(counter);check(world.seenTarget==game.field_1689&&world.seenWidth==1920&&number("sr.construct")==0&&!UpscaleSettings.active(),"100 percent without sharpening must bypass all GPU reconstruction");UpscaleSettings.apply(false,67,20);
UpscaleSettings.setEnabled(true);world.method_3188(counter);check(UpscaleSettings.active(),"actual SR active: "+UpscaleSettings.status());
check(world.seenWidth==1286&&world.seenHeight==722&&world.seenTarget!=game.field_1689,"scaled world target");
check(game.method_1522()==game.field_1689&&game.window.method_4489()==1920&&game.window.method_4506()==1080,"native target restored before HUD");
check(world.seenInput==960&&game.window.method_4507()==540&&world.otherThreadWidth==1920,"input and other threads unchanged");
check(number("sr.resolves")==1&&number("sr.nativeScope")==1,"upscale after leaving world scope");
int allocations=class_276.created;world.method_3188(counter);check(class_276.created==allocations&&number("sr.construct")==1,"cached resources");
UpscaleSettings.setSharpnessPercent(31);world.method_3188(counter);check(class_276.created==allocations,"sharpen does not resize");
UpscaleSettings.setScalePercent(50);world.method_3188(counter);check(world.seenWidth==960&&world.seenHeight==540&&class_276.created==allocations+1,"scale rebuild");
game.field_1689.method_1238();game.field_1689=new class_6367("native resized",1919,1079,true);game.window.field_5181=1919;game.window.field_5196=1079;UpscaleSettings.setScalePercent(100);world.method_3188(counter);
check(world.seenWidth==1919&&world.seenHeight==1079&&UpscaleSettings.info().outputWidth==1919,"100 percent preserves odd native size");
game.field_1689.method_1238();game.field_1689=new class_6367("native restored",1920,1080,true);game.window.field_5181=1920;game.window.field_5196=1080;UpscaleSettings.setScalePercent(50);world.method_3188(counter);
int calls=world.calls,resolves=number("sr.resolves");RuntimeException original=new IllegalArgumentException("original world");world.failure=original;
try{world.method_3188(counter);throw new AssertionError("swallowed exception");}catch(RuntimeException got){check(got==original,"same original exception");}
check(world.calls==calls+1&&number("sr.resolves")==resolves&&game.window.method_4489()==1920&&game.method_1522()==game.field_1689,"exception once, scope restored");world.failure=null;
net.irisshaders.iris.Iris.shaders=true;world.method_3188(counter);check(!UpscaleSettings.active()&&world.seenWidth==1920,"shader pipeline fail open");net.irisshaders.iris.Iris.shaders=false;
System.setProperty("sr.prepareFail","true");UpscaleSettings.setSharpnessPercent(32);world.method_3188(counter);check(!UpscaleSettings.active()&&world.seenWidth==1920,"prepare fails before lowering");
System.clearProperty("sr.prepareFail");world.method_3188(counter);check(world.seenWidth==1920,"failed revision latched");UpscaleSettings.setSharpnessPercent(33);world.method_3188(counter);check(UpscaleSettings.active(),"changed settings retry");
System.setProperty("sr.resolveFail","true");world.method_3188(counter);check(!UpscaleSettings.active()&&number("sr.fallback")>0,"GPU fallback before deactivation");System.clearProperty("sr.resolveFail");world.method_3188(counter);check(world.seenWidth==1920,"next frame native after resolve failure");
UpscaleSettings.setEnabled(false);world.method_3188(counter);check(number("sr.closed")==number("sr.construct")&&class_276.deleted==class_276.created-1,"release all scaled resources, native remains");
// Transform the original resources, not already defined classes. Unknown layouts must not install.
SuperResolutionTransformer tr=new SuperResolutionTransformer();ClassLoader isolated=new ClassLoader(){};
for(String n:new String[]{"net/minecraft/class_310","net/minecraft/class_1041","net/minecraft/class_757"}){byte[] b=bytes(n),p=tr.transform(isolated,n,null,null,b);check(p!=null,"verified shape "+n);check(tr.transform(isolated,n,null,null,p)==null,"idempotent "+n);check(tr.transform(isolated,n,String.class,null,b)==null,"no retransform");}
byte[] b=bytes("net/minecraft/class_1041");ClassWriter w=new ClassWriter(0);new ClassReader(b).accept(new ClassVisitor(Opcodes.ASM9,w){public MethodVisitor visitMethod(int a,String n,String d,String s,String[] x){return new MethodVisitor(Opcodes.ASM9,super.visitMethod(a,n,d,s,x)){public void visitInsn(int op){if(n.equals("method_4489")&&op==Opcodes.IRETURN){super.visitInsn(Opcodes.ICONST_1);super.visitInsn(Opcodes.IADD);}super.visitInsn(op);}};}},0);
check(tr.transform(isolated,"net/minecraft/class_1041",null,null,w.toByteArray())==null,"unknown getter rejected");
check(tr.transform(isolated,"java/lang/Thread",null,null,b)==null,"unrelated classes untouched");
System.out.println("PASS super resolution adapter: scope/HUD/input/threads/cache/resize/exception/fallback/cleanup/strict layouts");
}}
''',
}
for name in ('class_9779', 'class_9922', 'class_4184'):
    SOURCES['net/minecraft/' + name + '.java'] = 'package net.minecraft; public class ' + name + ' {}'
for package, name in (('org.joml', 'Matrix4f'), ('org.joml', 'Vector4f'), ('com.mojang.blaze3d.buffers', 'GpuBufferSlice')):
    SOURCES[package.replace('.', '/') + '/' + name + '.java'] = 'package ' + package + '; public class ' + name + ' {}'

FAKE = '''package pclmoon.framegen.render;
public class GlSuperResolutionRenderer {
static void inc(String k){System.setProperty(k,Integer.toString(Integer.parseInt(System.getProperty(k,"0"))+1));}
public GlSuperResolutionRenderer(){inc("sr.construct");}
public boolean prepare(int w,int h){return !Boolean.getBoolean("sr.prepareFail");}
public boolean upscale(int c,int d,int o,int z,int sw,int sh,int dw,int dh,float sharp){inc("sr.resolves");if(pclmoon.framegen.bridge.NativeUpscaleHooks.framebufferWidth(1920)==1920)inc("sr.nativeScope");if(Boolean.getBoolean("sr.resolveFail")){inc("sr.fallback");return false;}return true;}
public void close(){inc("sr.closed");}
}'''


def run(java, javac, jar, helper, asm, work):
    root = work / 'sr-adapter'
    root.mkdir()
    sources = root / 'sources'
    classes = root / 'classes'
    classes.mkdir()
    for name, text in SOURCES.items():
        path = sources / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')
    cp = os.pathsep.join(map(str, (jar, helper, asm)))
    subprocess.run([javac, '--release', '8', '-encoding', 'UTF-8', '-cp', cp, '-d', str(classes),
                    *map(str, sources.rglob('*.java'))], check=True)
    fake = root / 'GlSuperResolutionRenderer.java'
    fake.write_text(FAKE, encoding='utf-8')
    backend = root / 'backend'
    backend.mkdir()
    subprocess.run([javac, '--release', '8', '-cp', str(helper), '-d', str(backend), str(fake)], check=True)
    fakejar = root / 'fake-sr.jar'
    with zipfile.ZipFile(fakejar, 'w') as out:
        for item in backend.rglob('*.class'):
            out.write(item, item.relative_to(backend).as_posix())
    subprocess.run([java, '-Xms16m', '-Xmx128m', '-Dpclmoon.framegen.enabled=false', '-javaagent:' + str(jar),
                    '-cp', str(classes) + os.pathsep + cp, 'pclmoon.framegen.test.SuperResolutionHarness', str(fakejar)],
                   check=True, timeout=30, cwd=work)
    # A changed getter must leave the complete render path native, not merely fail a static check.
    win = sources / 'net/minecraft/class_1041.java'
    win.write_text(SOURCES['net/minecraft/class_1041.java'].replace('return field_5181;', 'return field_5181 + Integer.parseInt("0");'), encoding='utf-8')
    subprocess.run([javac, '--release', '8', '-d', str(classes), str(win)], check=True)
    subprocess.run([java, '-Xms16m', '-Xmx128m', '-Dpclmoon.framegen.enabled=false', '-javaagent:' + str(jar),
                    '-cp', str(classes) + os.pathsep + cp, 'pclmoon.framegen.test.SuperResolutionHarness', str(fakejar), 'unsupported'],
                   check=True, timeout=30, cwd=work)
