﻿module internal rec NBomber.Reporting

open System
open System.IO
open System.Collections.Generic
open HdrHistogram
open ConsoleTables

type Latency = int64
type ExceptionCount = int

type StepInfo = {
    StepName: string
    Latencies: Latency[]
    ThrownException: exn option
    OkCount: int
    FailCount: int
    ExceptionCount: ExceptionCount
} with
  static member Create(stepName, responseResults: List<Response*Latency>, 
                                 exceptions: (Option<exn>*ExceptionCount)) =     
    
    let results = responseResults.ToArray()
    
    { StepName = stepName 
      Latencies = results |> Array.map(snd)
      ThrownException = fst(exceptions)                                  
      OkCount = results 
                |> Array.map(fst)
                |> Array.filter(fun stRes -> stRes.IsOk)
                |> Array.length                                  
      FailCount = results 
                  |> Array.map(fst)
                  |> Array.filter(fun stRes -> not(stRes.IsOk))
                  |> Array.length
      ExceptionCount = snd(exceptions) }

  static member ToString(stepInfo: StepInfo) =
      String.Format("{0} (OK:{1}, Failed:{2}, Exceptions:{3})", stepInfo.Latencies.Length,
                    stepInfo.OkCount, stepInfo.FailCount, stepInfo.ExceptionCount)

type FlowInfo = {
    FlowName: string
    Steps: StepInfo[]    
    ConcurrentCopies: int
} with
  static member Create(flowName, concurrentCopies: int) (stepsStats: StepInfo[]) =
    // merge all steps results into one 
    let results = 
        stepsStats
        |> Array.groupBy(fun x -> x.StepName)
        |> Array.map(fun (stName,stRes) -> 
            { StepName = stName
              Latencies = stRes |> Array.collect(fun x -> x.Latencies)
              ThrownException = stRes |> Array.tryLast |> Option.bind(fun x -> x.ThrownException)
              OkCount = stRes |> Array.map(fun x -> x.OkCount) |> Array.sum
              FailCount = stRes |> Array.map(fun x -> x.FailCount) |> Array.sum
              ExceptionCount = stRes |> Array.map(fun x -> x.ExceptionCount) |> Array.sum })
    
    { FlowName = flowName; Steps = results; ConcurrentCopies = concurrentCopies }

type StepStats = {
    StepNo: int
    Info: StepInfo
    RPS: int64
    Min: Latency
    Mean: Latency
    Max: Latency
    Percent50: Latency
    Percent75: Latency
} with
  static member Create(stepNo: int, stepInfo: StepInfo, scenarioDuration: TimeSpan) = 
    let histogram = LongHistogram(TimeStamp.Hours(1), 3);
    stepInfo.Latencies |> Array.iter(fun x -> histogram.RecordValue(x))
        
    let rps = histogram.TotalCount / int64(scenarioDuration.TotalSeconds)

    let min = if stepInfo.Latencies.Length > 0 then Array.min(stepInfo.Latencies) else int64(0)
    let mean = if stepInfo.Latencies.Length > 0 then Convert.ToInt64(histogram.GetMean()) else int64(0) 
    let max  = if stepInfo.Latencies.Length > 0 then histogram.GetMaxValue() else int64(0)

    let percent50 = if stepInfo.Latencies.Length > 0 then histogram.GetValueAtPercentile(50.) else int64(0)
    let percent75 = if stepInfo.Latencies.Length > 0 then histogram.GetValueAtPercentile(75.) else int64(0)
    
    { StepNo = stepNo; Info = stepInfo; RPS = rps
      Min = min; Mean = mean; Max = max
      Percent50 = percent50; Percent75 = percent75 }


let buildReport (scenario: Scenario, flowInfo: FlowInfo[]) =        
    let header = String.Format("Scenario: {0}, execution time: {1}", scenario.ScenarioName, scenario.Duration.ToString())
    let details = flowInfo |> Array.mapi(fun i x -> printFlowTable(x, scenario.Duration, i+1)) |> String.concat Environment.NewLine    
    header + Environment.NewLine + Environment.NewLine + details                 

let printFlowTable (flowStats: FlowInfo, scenarioDuration: TimeSpan, flowCount: int) =
    
    let consoleTableOptions = 
        ConsoleTableOptions(Columns = [String.Format("flow {0}: {1}", flowCount, flowStats.FlowName)
                                       "steps"; String.Format("concurrent copies: {0}", flowStats.ConcurrentCopies)],
                            EnableCount = false)

    let flowTable = ConsoleTable(consoleTableOptions)
       
    flowStats.Steps
    |> Array.iteri(fun index stats -> flowTable.AddRow("", String.Format("{0} - {1}", index + 1, stats.StepName), "") |> ignore)

    let stepTable = ConsoleTable("step no", "requests", "RPS", "min", "mean", "max", "50%", "70%")
    flowStats.Steps
    |> Array.mapi(fun i stInfo -> StepStats.Create(i + 1, stInfo, scenarioDuration))
    |> Array.iter(fun s -> stepTable.AddRow(s.StepNo, StepInfo.ToString(s.Info),
                                            s.RPS, s.Min, s.Mean, s.Max, s.Percent50, s.Percent75) |> ignore)
    
    flowTable.ToString() + stepTable.ToStringAlternative() + Environment.NewLine + Environment.NewLine

let saveReport (report: string) = 
    Directory.CreateDirectory("reports") |> ignore
    let filePath = Path.Combine("reports", "report-" + DateTime.UtcNow.ToString("yyyy-dd-M--HH-mm-ss")) + ".txt"
    File.WriteAllText(filePath, report)