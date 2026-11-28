# Planner

Crimson Banner OS's planning app, in Software's Crimson Banner tab (`crimson-planner`). It's an advisory **Economic
Planning Algorithm**, after *The Economic Structure*. It proposes quotas from what local councils report, and councils
decide. Every figure is plain arithmetic that a council can trace back to the reports. Nothing goes over the network.

## Simple and advanced views

Planner opens in its **simple view**, for anyone. One page, "This period", goes through a period in the order it
happens: hand in the councils' reports, check what stands out, decide each product (agree, change or reject Planner's
figure), and send each council its share. "How it works" explains the process without the arithmetic. The
**advanced view** has every figure, table and setting described below. The switch is at the bottom of the sidebar,
and Planner remembers the choice (`~/.config/crimson-planner/view`). Decisions made in either view are the same
decisions.

## How a plan is worked out

The three steps follow the document's computational logging, sorting and execution.

**1. Logging.** Each local council reports, per product, what was made, what people took out of stock (sold or handed
out: the period's final demand) and what's left in stock. A report is a CSV file
(`council,period,product,produced,depleted,stock`); one file can hold a whole region's. Every report is sealed in the
**verification ledger** as it's taken in: a SHA-256 of its figures, chained to the entry before it. A report changed
later no longer matches its entry, and an entry changed or removed breaks the chain.

**2. Sorting.** For each product, across all councils:

- **Turnover** = depleted ÷ the average stock held. Against its target (1 ÷ the periods of stock to hold), it's the
  product's shadow price: *running short* above the target band, *overstocked* below it.
- **Expected demand** is depletion smoothed with the periods before (exponential smoothing,
  `E = α·depleted + (1 − α)·E_previous`): the document's "inference from previous entries".
- **Final demand** = expected demand + (the stock to hold − the stock left) ÷ the correction periods. That's what's
  needed next period, with short stock made up and surplus run down.

**3. Execution.**

- **Need:** Leontief's input–output model, `x = (I − A)⁻¹ d`. A says how much of each product making one unit of
  another uses up, so x is what every sector must make to meet final demand *and* each other's inputs. The economy
  must be productive: A's largest eigenvalue under 1.
- **Labour per unit:** `v = l (I − A)⁻¹`, the hours each product takes, directly and through its inputs (labour-time
  accounting).
- **What fits:** a product over its councils' combined capacity has its own final demand cut until it fits. If labour
  falls short, the whole plan is scaled to the hours there are. The proposal is what fits, and the rest shows as a
  shortfall.
- **Recounts:** a council's report far from its own history (three periods at least) or from its peers that period
  (four councils at least) is flagged for a recount. "Far" is robust deviations, using the median and the median
  absolute deviation, with a floor of 10% of the median, so ordinary growth isn't flagged. A report that no longer
  matches the ledger is flagged too.
- **Advisory, not authority:** every quota is a proposal until a council **ratifies**, **amends** or **rejects** it
  (rejected: the last enacted quota stands). A **major shift** needs a quorum of votes in favour. That's a change from
  the last quota enacted beyond the threshold (10% by default), or a first quota.
- **Down to councils:** enacted quotas are shared among the local councils in proportion to their capacity, region by
  region. They're exported as one CSV per council, ready for its workplaces.

Where the document speaks of A.I., Planner uses these transparent methods instead, so that every quota can be
explained to the council deciding it. Encrypting plan files is left for a later version.

## Files

- Plans are saved after every change to `~/Documents/Plans/NAME.plan.json`: the one file every report is compiled
  into, with the ledger and the decisions.
- Reports can be dropped on the window, given by path, or put in `~/Documents/Plans/Reports`. Reports → "Write a blank
  report form" writes one to fill in.
- Quotas are exported to `~/Documents/Plans/Quotas/PLAN/period-N/`: everything in one file, and a file per council in
  its region's folder.

An example economy is included, with five sectors, two regions and four periods. The last period has one report
deliberately out of line, to show a recount.

## Building

The source is in `src/` (C#, Avalonia, sharing the shell's `Styles/Shell.axaml`). The engine and window are tested
with the shell's tests (`tests/RedStarOS.Tests/PlannerTests.cs`, and screenshots of every page).

```bash
dotnet run --project apps/planner/src          # from the tree
tools/publish-app.sh apps/planner              # build, sign and publish it to Software
```
