$ErrorActionPreference='Stop'
$source='4d73916be5f997c26ee9f14d2103d9a6ddd2a5c5'
if((git rev-parse HEAD) -ne $source){throw 'Wrong exact source'}
$root=(New-Item -ItemType Directory -Force ingress-red-receipts).FullName
$groups=@(
 @{name='Network_Route_Carries_Exact_Attempt_And_Same_Publication_To_Recipient';count=3;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Injection_Has_Server_Issued_Origin_Without_Fabricated_Network_Attempt';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Native_Will_Has_Dedicated_Ownership_After_Origin_Disconnect';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Network_And_Server_Subscribe_Replay_Carry_Snapshot_Authority';count=2;message='Actual InterceptingClientApplicationMessageEnqueueEventArgs route has no native PublicationContext surface.'},
 @{name='Retained_Replacement_Cannot_Rewrite_Already_Captured_Replay';count=2;message='Actual InterceptingClientApplicationMessageEnqueueEventArgs route has no native PublicationContext surface.'},
 @{name='Takeover_Fences_Late_Callback_Before_Retained_Write_And_Recipient_Enqueue';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'},
 @{name='Recipient_Takeover_Revokes_Exact_Execution_Before_Late_Enqueue';count=2;message='Publication context is missing RecipientExecution.'},
 @{name='Disabled_Mode_Legacy_Constructors_And_Injection_Still_Deliver';count=1;green=$true},
 @{name='Durable_Acceptance_Carries_Native_Publication_Context';count=1;message='Actual AcceptingIncomingQos2MessageEventArgs route has no native PublicationContext surface.'},
 @{name='Memory_Reconnect_Preserves_One_Admission_But_Not_Old_Execution_Lease';count=1;message='Actual InterceptingPublishEventArgs route has no native PublicationContext surface.'}
)
@{source=$source;hardProcessLimitMilliseconds=5000;groups=$groups;scope='Expected missing native capability failures only; compile failure, timeout, missing TRX, wrong count or other assertion is unexpected';artifactRetentionDays=7} | ConvertTo-Json -Depth 6 | Set-Content "$root/EXPECTED-RESULTS.json"
dotnet build Source/MQTTnet.Tests/MQTTnet.Tests.csproj -c Release -m:2 -p:BuildInParallel=false -p:AssemblyVersion=1.0.0.0 -p:Version=5.2.0-local.ingress.4d73916b -p:SourceRevisionId=4d73916be5f997c26ee9f14d2103d9a6ddd2a5c5 *> "$root/build.log"
if($LASTEXITCODE -ne 0){@{source=$source;classification='CompilationFailure';testsExecuted=$false} | ConvertTo-Json | Set-Content "$root/RESULT.json";throw 'Compilation failure, not expected red'}
$results=@();$dlls=@()
foreach($tfm in @('net8.0','net10.0')){
 $bin=(Resolve-Path "Source/MQTTnet.Tests/bin/Release/$tfm").Path
 foreach($lib in @('MQTTnet','MQTTnet.Server','MQTTnet.AspNetCore','MQTTnet.Tests')){$dlls+=@{framework=$tfm;library=$lib;sha256=(Get-FileHash "$bin/$lib.dll").Hash}}
 foreach($g in $groups){
  $stem="$tfm-$($g.name)";$trx="$root/$stem.trx"
  $args=@("$bin/MQTTnet.Tests.dll",'--filter',"FullyQualifiedName~$($g.name)",'--report-trx','--report-trx-filename',"$stem.trx",'--results-directory',$root,'--diagnostic','--diagnostic-verbosity','Trace','--diagnostic-file-prefix',$stem,'--diagnostic-output-directory',"$root/diagnostics/$stem")
  $p=Start-Process dotnet -ArgumentList $args -PassThru -RedirectStandardOutput "$root/$stem.stdout.log" -RedirectStandardError "$root/$stem.stderr.log"
  $finished=$p.WaitForExit(5000)
  if(!$finished){$p.Kill($true);$p.WaitForExit();$results+=@{framework=$tfm;name=$g.name;classification='UnexpectedProcessTimeout';expected=$false};continue}
  $p.Refresh();$code=$p.ExitCode
  if(!(Test-Path $trx)){$results+=@{framework=$tfm;name=$g.name;classification='UnexpectedMissingTrx';exit=$code;expected=$false};continue}
  [xml]$x=Get-Content $trx;$c=$x.TestRun.ResultSummary.Counters
  $rows=@($x.TestRun.Results.UnitTestResult)
  $failed=@($rows | Where-Object outcome -eq 'Failed')
  $messages=@($failed | ForEach-Object {[string]$_.Output.ErrorInfo.Message})
  $rowChecks=foreach($row in $rows){
   $definition=@($x.TestRun.TestDefinitions.UnitTest | Where-Object id -eq $row.testId)
   $validDefinition=$definition.Count -eq 1 -and $definition[0].TestMethod.name -eq $g.name -and $definition[0].TestMethod.className -match '^MQTTnet\.Tests\.Server\.(TrustedIngressContext_Tests|IncomingQos2Persistence_Tests)(,|$)'
   $validName=[string]$row.testName -match ('^'+[regex]::Escape($g.name)+'(?:\s*\(.*\))?$')
   @{name=[string]$row.testName;testId=[string]$row.testId;methodAndClassMatch=[bool]$validDefinition;rowNameMatchesGroup=[bool]$validName}
  }
  $diagnosticFiles=@(Get-ChildItem "$root/diagnostics/$stem" -File -Recurse -ErrorAction SilentlyContinue)
  $diagnostics=(Get-Content "$root/$stem.stdout.log" -Raw)+(Get-Content "$root/$stem.stderr.log" -Raw)
  foreach($file in $diagnosticFiles){$diagnostics+=Get-Content $file.FullName -Raw}
  $exceptionType='Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException'
  $assertionChecks=foreach($row in $failed){
   $message=[string]$row.Output.ErrorInfo.Message;$stack=[string]$row.Output.ErrorInfo.StackTrace
   $pattern='^(?:'+[regex]::Escape($exceptionType)+':\s*)?Assert\.IsNotNull failed\.\s*'+[regex]::Escape($g.message)+'(?:\s*''value'' expression:\s*''property''\.)?\s*$'
   $helper=if($g.name -eq 'Recipient_Takeover_Revokes_Exact_Execution_Before_Late_Enqueue'){'RequireProperty'}else{'RequireContext'}
   $stackMatches=$stack -match 'Microsoft\.VisualStudio\.TestTools\.UnitTesting\.Assert\.(?:IsNotNull|ThrowAssertIsNotNullFailed|ThrowAssertFailed)' -and $stack -match ('MQTTnet\.Tests\.Server\.TrustedIngressContext_Tests\.'+$helper+'\(')
   # TRX has no serialized exception-type field. Require the actual runtime type
   # in the original MTP diagnostic output as well, rather than inventing one.
   $typeObserved=[regex]::IsMatch($diagnostics, [regex]::Escape($exceptionType)+':'+'\s*'+[regex]::Escape($message))
   @{name=[string]$row.testName;exactMissingCapabilityMessage=[bool]($message -match $pattern);mstestAssertionAndNativeHelperStack=[bool]$stackMatches;actualAssertFailedExceptionTypeInDiagnostics=[bool]$typeObserved;exceptionType=$(if($typeObserved){$exceptionType}else{$null});message=$message;stack=$stack}
  }
  $ok=$c.total -eq [string]$g.count -and $c.notExecuted -eq '0' -and $rows.Count -eq $g.count -and @($rows.testName | Sort-Object -Unique).Count -eq $g.count -and @($rowChecks | Where-Object {!$_.methodAndClassMatch -or !$_.rowNameMatchesGroup}).Count -eq 0
  if($g.green){$ok=$ok -and $code -eq 0 -and $c.passed -eq [string]$g.count -and $c.failed -eq '0'}else{
   $ok=$ok -and $code -eq 2 -and $c.failed -eq [string]$g.count -and $c.passed -eq '0' -and $failed.Count -eq $g.count -and @($assertionChecks | Where-Object {!$_.exactMissingCapabilityMessage -or !$_.mstestAssertionAndNativeHelperStack -or !$_.actualAssertFailedExceptionTypeInDiagnostics}).Count -eq 0
  }
  $results+=@{framework=$tfm;name=$g.name;classification=$(if($ok){if($g.green){'LegacyControlGreen'}else{'ExpectedMissingCapability'}}else{'UnexpectedTestResult'});exit=$code;expected=[bool]$ok;counters=$c.OuterXml;messages=$messages;rowChecks=$rowChecks;assertionChecks=$assertionChecks;diagnosticFiles=@($diagnosticFiles | ForEach-Object {@{name=$_.Name;sha256=(Get-FileHash $_.FullName).Hash}});trxSha256=(Get-FileHash $trx).Hash}
 }
}
@{source=$source;dlls=$dlls;results=$results;allExpected=(@($results | Where-Object expected -ne $true).Count -eq 0)} | ConvertTo-Json -Depth 10 | Set-Content "$root/RESULT.json"
if(@($results | Where-Object expected -ne $true).Count){throw 'Unexpected ingress probe result; see classified receipts'}
$global:LASTEXITCODE=0
