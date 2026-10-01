"""Root-owned assigned Windows compile/API-only phase. Preparation is inert; no TLS or package run."""
import argparse, ctypes as C, hashlib, json, msvcrt, os, shutil, subprocess, sys, threading, time, uuid, zipfile
from pathlib import Path
from ctypes import wintypes as W
HERE=Path(__file__).resolve().parent
MAX_DISK=2*1024**3; MAX_LOG=16*1024**2; WHOLE_SECONDS=600

def sha(path):
 h=hashlib.sha256()
 with open(path,'rb') as f:
  for block in iter(lambda:f.read(1024*1024),b''):h.update(block)
 return h.hexdigest().upper()
def read(path):return json.loads(Path(path).read_text(encoding='utf-8-sig'))
def save(path,value):
 path=Path(path);temporary=path.with_name(path.name+'.tmp');temporary.write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8');os.replace(temporary,path)
def verify(records):
 for r in records:
  if sha(r['path'])!=r['sha256'].upper():raise RuntimeError('Hash mismatch: '+r['path'])
def disk(path):return sum(p.stat().st_size for p in path.rglob('*') if p.is_file())

def safe_unzip(archive,target):
 with zipfile.ZipFile(archive) as z:
  for i in z.infolist():
   dest=(target/i.filename).resolve()
   if not dest.is_relative_to(target.resolve()):raise RuntimeError('Archive path escapes owned stage')
   if i.is_dir():dest.mkdir(parents=True,exist_ok=True);continue
   dest.parent.mkdir(parents=True,exist_ok=True)
   with z.open(i) as a,open(dest,'wb') as b:shutil.copyfileobj(a,b)

def retain_build_evidence(stage,receipts,stem):
 retained=receipts/'build-evidence'/stem;records=[]
 for path in sorted(stage.rglob('*')):
  if not path.is_file():continue
  relative=path.relative_to(stage)
  if not any(part in ('bin','obj') for part in relative.parts):continue
  if not (path.suffix.lower() in ('.dll','.pdb','.json') or path.name.endswith(('.nuget.g.props','.nuget.g.targets','.AssemblyInfo.cs','.AssemblyAttributes.cs'))):continue
  destination=retained/relative;destination.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(path,destination)
  records.append({'sourceRelativePath':relative.as_posix(),'retainedPath':str(destination),'bytes':destination.stat().st_size,'sha256':sha(destination)})
 save(receipts/(stem+'-retained-evidence.json'),records)
 return records


class IO_COUNTERS(C.Structure):
 _fields_=[(n,C.c_ulonglong) for n in ('ReadOperationCount','WriteOperationCount','OtherOperationCount','ReadTransferCount','WriteTransferCount','OtherTransferCount')]
class BASIC_LIMIT(C.Structure):
 _fields_=[('PerProcessUserTimeLimit',C.c_longlong),('PerJobUserTimeLimit',C.c_longlong),('LimitFlags',W.DWORD),('MinimumWorkingSetSize',C.c_size_t),('MaximumWorkingSetSize',C.c_size_t),('ActiveProcessLimit',W.DWORD),('Affinity',C.c_size_t),('PriorityClass',W.DWORD),('SchedulingClass',W.DWORD)]
class EXTENDED_LIMIT(C.Structure):
 _fields_=[('BasicLimitInformation',BASIC_LIMIT),('IoInfo',IO_COUNTERS),('ProcessMemoryLimit',C.c_size_t),('JobMemoryLimit',C.c_size_t),('PeakProcessMemoryUsed',C.c_size_t),('PeakJobMemoryUsed',C.c_size_t)]
class BASIC_ACCOUNTING(C.Structure):
 _fields_=[('TotalUserTime',C.c_longlong),('TotalKernelTime',C.c_longlong),('ThisPeriodTotalUserTime',C.c_longlong),('ThisPeriodTotalKernelTime',C.c_longlong),('TotalPageFaultCount',W.DWORD),('TotalProcesses',W.DWORD),('ActiveProcesses',W.DWORD),('TotalTerminatedProcesses',W.DWORD)]
class STARTUP(C.Structure):
 _fields_=[('cb',W.DWORD),('lpReserved',W.LPWSTR),('lpDesktop',W.LPWSTR),('lpTitle',W.LPWSTR),('dwX',W.DWORD),('dwY',W.DWORD),('dwXSize',W.DWORD),('dwYSize',W.DWORD),('dwXCountChars',W.DWORD),('dwYCountChars',W.DWORD),('dwFillAttribute',W.DWORD),('dwFlags',W.DWORD),('wShowWindow',W.WORD),('cbReserved2',W.WORD),('lpReserved2',C.POINTER(C.c_ubyte)),('hStdInput',W.HANDLE),('hStdOutput',W.HANDLE),('hStdError',W.HANDLE)]
class STARTUP_EX(C.Structure):
 _fields_=[('StartupInfo',STARTUP),('lpAttributeList',C.c_void_p)]
class PROCESS_INFO(C.Structure):
 _fields_=[('hProcess',W.HANDLE),('hThread',W.HANDLE),('dwProcessId',W.DWORD),('dwThreadId',W.DWORD)]

def api():
 k=C.WinDLL('kernel32',use_last_error=True)
 for name,ret,args in [
 ('InitializeProcThreadAttributeList',W.BOOL,[C.c_void_p,W.DWORD,W.DWORD,C.POINTER(C.c_size_t)]),('UpdateProcThreadAttribute',W.BOOL,[C.c_void_p,W.DWORD,C.c_size_t,C.c_void_p,C.c_size_t,C.c_void_p,C.c_void_p]),('DeleteProcThreadAttributeList',None,[C.c_void_p]),('IsProcessInJob',W.BOOL,[W.HANDLE,W.HANDLE,C.POINTER(W.BOOL)]),
 ('CreateJobObjectW',W.HANDLE,[C.c_void_p,W.LPCWSTR]),('SetInformationJobObject',W.BOOL,[W.HANDLE,C.c_int,C.c_void_p,W.DWORD]),
 ('AssignProcessToJobObject',W.BOOL,[W.HANDLE,W.HANDLE]),('ResumeThread',W.DWORD,[W.HANDLE]),
 ('CreateProcessW',W.BOOL,[W.LPCWSTR,W.LPWSTR,C.c_void_p,C.c_void_p,W.BOOL,W.DWORD,C.c_void_p,W.LPCWSTR,C.POINTER(STARTUP),C.POINTER(PROCESS_INFO)]),
 ('WaitForSingleObject',W.DWORD,[W.HANDLE,W.DWORD]),('GetExitCodeProcess',W.BOOL,[W.HANDLE,C.POINTER(W.DWORD)]),
 ('TerminateJobObject',W.BOOL,[W.HANDLE,W.UINT]),('TerminateProcess',W.BOOL,[W.HANDLE,W.UINT]),
 ('QueryInformationJobObject',W.BOOL,[W.HANDLE,C.c_int,C.c_void_p,W.DWORD,C.POINTER(W.DWORD)]),('CloseHandle',W.BOOL,[W.HANDLE])]:
  fn=getattr(k,name);fn.restype=ret;fn.argtypes=args
 return k

def run_job(k,command,cwd,env,seconds,stem,receipts,work,whole_start):
 job_name='Local\\MQTTnet-Context-API-'+str(uuid.uuid4())
 job=k.CreateJobObjectW(None,job_name)
 if not job:raise C.WinError(C.get_last_error())
 lim=EXTENDED_LIMIT();lim.BasicLimitInformation.LimitFlags=0x2000|0x8|0x100|0x200
 lim.BasicLimitInformation.ActiveProcessLimit=16;lim.ProcessMemoryLimit=1536*1024**2;lim.JobMemoryLimit=2*1024**3
 if not k.SetInformationJobObject(job,9,C.byref(lim),C.sizeof(lim)):
  k.CloseHandle(job);raise C.WinError(C.get_last_error())
 save(receipts/(stem+'.job.json'),{'jobName':job_name,'phase':'PRIVATE_JOB_CREATED_BEFORE_PROCESS'})
 attributes=C.c_size_t(0);k.InitializeProcThreadAttributeList(None,1,0,C.byref(attributes))
 attribute_buffer=C.create_string_buffer(attributes.value);attribute_pointer=C.cast(attribute_buffer,C.c_void_p)
 if not k.InitializeProcThreadAttributeList(attribute_pointer,1,0,C.byref(attributes)):
  k.CloseHandle(job);raise C.WinError(C.get_last_error())
 job_list=(W.HANDLE*1)(job)
 if not k.UpdateProcThreadAttribute(attribute_pointer,0,0x0002000D,C.cast(job_list,C.c_void_p),C.sizeof(job_list),None,None):
  k.DeleteProcThreadAttributeList(attribute_pointer);k.CloseHandle(job);raise C.WinError(C.get_last_error())
 info=PROCESS_INFO();result={'stem':stem,'command':command,'limitSeconds':seconds,'ownedWindowsJob':True,'jobName':job_name};started=False;error=None
 try:
  with open(receipts/(stem+'.stdout.log'),'wb') as stdout,open(receipts/(stem+'.stderr.log'),'wb') as stderr,open(os.devnull,'rb') as stdin:
   for f in (stdout,stderr,stdin):os.set_inheritable(f.fileno(),True)
   extended=STARTUP_EX();extended.lpAttributeList=attribute_pointer;start=extended.StartupInfo;start.cb=C.sizeof(extended);start.dwFlags=0x100
   start.hStdInput=msvcrt.get_osfhandle(stdin.fileno());start.hStdOutput=msvcrt.get_osfhandle(stdout.fileno());start.hStdError=msvcrt.get_osfhandle(stderr.fileno())
   block=C.create_unicode_buffer('\0'.join(key+'='+str(value) for key,value in sorted(env.items()))+'\0\0')
   line=C.create_unicode_buffer(subprocess.list2cmdline(command))
   if not k.CreateProcessW(command[0],line,None,None,True,0x4|0x400|0x08000000|0x00080000,C.cast(block,C.c_void_p),str(cwd),C.cast(C.byref(extended),C.POINTER(STARTUP)),C.byref(info)):raise C.WinError(C.get_last_error())
   started=True;result['pid']=info.dwProcessId
   save(receipts/(stem+'.launch.json'),{'pid':int(info.dwProcessId),'jobName':job_name,'phase':'ATOMICALLY_CREATED_IN_JOB_NOT_RESUMED'})
   # JOB_LIST binds at creation; controller death has no unassigned-process gap.
   assigned=W.BOOL()
   if not k.IsProcessInJob(info.hProcess,job,C.byref(assigned)) or not assigned.value:raise RuntimeError('Atomic job assignment was not confirmed')
   save(receipts/(stem+'.launch.json'),{'pid':int(info.dwProcessId),'jobName':job_name,'phase':'JOB_ASSIGNED_BEFORE_RESUME'})
   if k.ResumeThread(info.hThread)==0xFFFFFFFF:raise C.WinError(C.get_last_error())
   t=time.monotonic();max_disk=0;reason=None
   while k.WaitForSingleObject(info.hProcess,200)==0x102:
    used=disk(work)+disk(receipts);max_disk=max(max_disk,used)
    if time.monotonic()-t>seconds:reason='ProcessTimeout'
    elif time.monotonic()-whole_start>WHOLE_SECONDS:reason='WholeSlotTimeout'
    elif used>MAX_DISK:reason='DiskBudgetExceeded'
    elif (receipts/(stem+'.stdout.log')).stat().st_size>MAX_LOG or (receipts/(stem+'.stderr.log')).stat().st_size>MAX_LOG:reason='LogBudgetExceeded'
    if reason:break
   observed_disk=disk(work)+disk(receipts);max_disk=max(max_disk,observed_disk)
   if observed_disk>MAX_DISK:reason=reason or 'DiskBudgetExceeded'
   if (receipts/(stem+'.stdout.log')).stat().st_size>MAX_LOG or (receipts/(stem+'.stderr.log')).stat().st_size>MAX_LOG:reason=reason or 'LogBudgetExceeded'
   if time.monotonic()-whole_start>WHOLE_SECONDS:reason=reason or 'WholeSlotTimeout'
   if reason:k.TerminateJobObject(job,124)
   if k.WaitForSingleObject(info.hProcess,10000)!=0:raise RuntimeError('Owned main process did not exit')
   code=W.DWORD()
   if not k.GetExitCodeProcess(info.hProcess,C.byref(code)):raise C.WinError(C.get_last_error())
   accounting=BASIC_ACCOUNTING();deadline=time.monotonic()+10
   while True:
    if not k.QueryInformationJobObject(job,1,C.byref(accounting),C.sizeof(accounting),None):raise C.WinError(C.get_last_error())
    if not accounting.ActiveProcesses:break
    k.TerminateJobObject(job,125)
    if time.monotonic()>deadline:raise RuntimeError('Owned child processes did not exit')
    time.sleep(.1)
   peak=EXTENDED_LIMIT()
   if not k.QueryInformationJobObject(job,9,C.byref(peak),C.sizeof(peak),None):raise C.WinError(C.get_last_error())
   result.update(exit=int(code.value),reason=reason,elapsedSeconds=time.monotonic()-t,activeProcesses=int(accounting.ActiveProcesses),totalProcesses=int(accounting.TotalProcesses),maxDiskObserved=max_disk,peakJobMemoryBytes=int(peak.PeakJobMemoryUsed),stdoutSha256=sha(receipts/(stem+'.stdout.log')),stderrSha256=sha(receipts/(stem+'.stderr.log')))
 except Exception as exception:
  error=str(exception);result['failure']=error
 finally:
  if started:
   # Includes create/assignment failures before ResumeThread.
   k.TerminateJobObject(job,126);k.TerminateProcess(info.hProcess,126);k.WaitForSingleObject(info.hProcess,10000)
   code=W.DWORD();k.GetExitCodeProcess(info.hProcess,C.byref(code));result.setdefault('exit',int(code.value))
   remaining=BASIC_ACCOUNTING()
   if k.QueryInformationJobObject(job,1,C.byref(remaining),C.sizeof(remaining),None):result['activeProcessesAfterTermination']=int(remaining.ActiveProcesses)
   k.CloseHandle(info.hThread);k.CloseHandle(info.hProcess)
  k.DeleteProcThreadAttributeList(attribute_pointer);k.CloseHandle(job);result['jobHandleClosed']=True
  save(receipts/(stem+'.process.json'),result)
 if error:raise RuntimeError(stem+' failed: '+error+'; owned job closed, original logs preserved')
 if result.get('reason') or result.get('exit')!=0:raise RuntimeError(stem+' failed; original logs preserved')
 return result
