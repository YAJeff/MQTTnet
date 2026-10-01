$ErrorActionPreference='Stop'
$source='f0e686821a76efc61dd9029b6e9ce12bf60c4150'
if((git rev-parse HEAD) -ne $source){throw 'Wrong exact source'}
$root=(New-Item -ItemType Directory -Force ingress-red-receipts).FullName
$groups=@(
 @{name='Network_Route_Carries_Exact_Attempt_And_Same_Publication_To_Recipient';count=3;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Injection_Has_Server_Issued_Origin_Without_Fabricated_Network_Attempt';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Native_Will_Has_Dedicated_Ownership_After_Origin_Disconnect';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Network_And_Server_Subscribe_Replay_Carry_Snapshot_Authority';count=2;message='Actual InterceptingClientEnqueueEventArgs route has no native PublicationContext surface.'},
 @{name='Retained_Replacement_Cannot_Rewrite_Already_Captured_Replay';count=2;message='Actual InterceptingClientEnqueueEventArgs route has no native PublicationContext surface.'},
 @{name='Takeover_Fences_Late_Callback_Before_Retained_Write_And_Recipient_Enqueue';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Recipient_Takeover_Revokes_Exact_Execution_Before_Late_Enqueue';count=2;message='Publication context is missing RecipientExecution.'},
 @{name='Disabled_Mode_Legacy_Constructors_And_Injection_Still_Deliver';count=1;green=$true},
 @{name='Durable_Acceptance_Carries_Native_Publication_Context';count=1;message='Actual AcceptingIncomingQos2MessageEventArgs route has no native PublicationContext surface.'},
 @{name='Memory_Reconnect_Preserves_One_Admission_But_Not_Old_Execution_Lease';count=1;message='Actual AcceptingIncomingQos2MessageEventArgs route has no native PublicationContext surface.'}
)
@{source=$source;hardProcessLimitMilliseconds=5000;groups=$groups;scope='Expected missing native capability failures only; compile failure, timeout, missing TRX, wrong count or other assertion is unexpected';artifactRetentionDays=7} | ConvertTo-Json -Depth 6 | Set-Content "$root/EXPECTED-RESULTS.json"
dotnet build Source/MQTTnet.Tests/MQTTnet.Tests.csproj -c Release -m:2 -p:BuildInParallel=false -p:AssemblyVersion=1.0.0.0 -p:Version=5.2.0-local.ingress.f0e68682 -p:SourceRevisionId=f0e686821a76efc61dd9029b6e9ce12bf60c4150 *> "$root/build.log"
if($LASTEXITCODE -ne 0){@{source=$source;classification='CompilationFailure';testsExecuted=$false} | ConvertTo-Json | Set-Content "$root/RESULT.json";throw 'Compilation failure, not expected red'}
$results=@();$dlls=@()
foreach($tfm in @('net8.0','net10.0')){
 $bin=(Resolve-Path "Source/MQTTnet.Tests/bin/Release/$tfm").Path
 foreach($lib in @('MQTTnet','MQTTnet.Server','MQTTnet.AspNetCore','MQTTnet.Tests')){$dlls+=@{framework=$tfm;library=$lib;sha256=(Get-FileHash "$bin/$lib.dll").Hash}}
 foreach($g in $groups){
  $stem="$tfm-$($g.name)";$trx="$root/$stem.trx"
  $args=@("$bin/MQTTnet.Tests.dll",'--filter',"FullyQualifiedName~$($g.name)",'--report-trx','--report-trx-filename',"$stem.trx",'--results-directory',$root)
  $p=Start-Process dotnet -ArgumentList $args -PassThru -RedirectStandardOutput "$root/$stem.stdout.log" -RedirectStandardError "$root/$stem.stderr.log"
  $finished=$p.WaitForExit(5000)
  if(!$finished){$p.Kill($true);$p.WaitForExit();$results+=@{framework=$tfm;name=$g.name;classification='UnexpectedProcessTimeout';expected=$false};continue}
  $p.Refresh();$code=$p.ExitCode
  if(!(Test-Path $trx)){$results+=@{framework=$tfm;name=$g.name;classification='UnexpectedMissingTrx';exit=$code;expected=$false};continue}
  [xml]$x=Get-Content $trx;$c=$x.TestRun.ResultSummary.Counters
  $failed=@($x.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed')
  $messages=@($failed | ForEach-Object {[string]$_.Output.ErrorInfo.Message})
  $ok=($c.total -eq [string]$g.count -and $c.notExecuted -eq '0')
  if($g.green){$ok=$ok -and $code -eq 0 -and $c.passed -eq [string]$g.count}else{$ok=$ok -and $code -ne 0 -and $c.failed -eq [string]$g.count -and @($messages | Where-Object {!$_.Contains($g.message)}).Count -eq 0}
  $results+=@{framework=$tfm;name=$g.name;classification=$(if($ok){if($g.green){'LegacyControlGreen'}else{'ExpectedMissingCapability'}}else{'UnexpectedTestResult'});exit=$code;expected=[bool]$ok;counters=$c.OuterXml;messages=$messages;trxSha256=(Get-FileHash $trx).Hash}
 }
}
@{source=$source;dlls=$dlls;results=$results;allExpected=(@($results | Where-Object expected -ne $true).Count -eq 0)} | ConvertTo-Json -Depth 10 | Set-Content "$root/RESULT.json"
if(@($results | Where-Object expected -ne $true).Count){throw 'Unexpected ingress probe result; see classified receipts'}
$global:LASTEXITCODE=0
