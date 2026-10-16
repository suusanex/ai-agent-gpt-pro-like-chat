# Pro-like 実行役

Issue #1 と #2 の段取りを、このリポジトリの File-based App が実行する。各役割の1回の実行は、公開済みの `CodexAgentFacadeCli` へ渡す。Copilot SDK は呼ばない。段取りの判断は [plans/pro-like-protocol-runner.md](../plans/pro-like-protocol-runner.md) と [issue #1 の決定コメント](https://github.com/suusanex/ai-agent-gpt-pro-like-chat/issues/1#issuecomment-6206956130) に従う。

## 起動

```powershell
dotnet run --file src/ProLikeRunner.cs -- self-test
dotnet run --file src/ProLikeRunner.cs -- run --config config\experiment.example.json --question question.md --out D:\temp\pro-like-run-001
dotnet run --file src/ProLikeRunner.cs -- baseline --config config\experiment.example.json --question question.md --out D:\temp\pro-like-baseline-001
dotnet run --file src/ProLikeRunner.cs -- continue --run-dir D:\temp\pro-like-run-001 --downstream both
```

`--downstream` は `pro-like`、`fable-like`、`both` のいずれかである。`run` は前段を1回実行し、同じ成果物から `final-pro-like.md` と `final-fable-like.md` の両方を書く。`baseline` はその前段を使わない1回の質問である。`continue` は保存済みの前段を再実行しない。

終了コードは、成功が 0、実行の失敗が 1、引数不足・未知のコマンド・未知のオプションが 2 である。Ctrl+C は起動中の Facade CLI を止めてから runner が戻る。同じ run directory を同時に実行した場合は、後から開始した側を失敗させる。

## Facade CLI の呼び方

実行ファイルは設定の `facadeCliPath` である。常駐 MCP は使わない。引数は渡さない。標準入力へ snake_case の JSON を1個書き、stdin を閉じて、標準出力の JSON を1個読む。

送るフィールドは `agent`、`prompt`、`working_directory`、`model`、`timeout_seconds`、`auto_approve` である。設定にあるときだけ `reasoning_effort` と `fast` を足す。`session_id`、`request_id`、`skills` は送らない。

成功はプロセス終了コード 0 かつ `status=completed` で、`result.outputText` が空でないことである。`outputKind` が `assistant_transcript` でも本文は採用する。それ以外は、その役割の失敗として実行全体を止める。`failure.kind` と `failure.summary`、および stderr は trace へ残す。起動失敗の診断は stderr にしか無いため、バイト数だけにはしない。実行中の例外は、その run の trace を閉じる前に `Exception.ToString()` を書く。

再実行する役割は、成功するまで `result.json`、`output.md`、公開する成果物を残さない。`fable-like` は adjudicator を始める時点で、前回の `final-fable-like.md` も消す。失敗した試みの prompt と request は、その役割の directory に残る。

`working_directory` はリポジトリの外に、呼び出しごとに新しい空 directory として作る。既定の場所は `%LOCALAPPDATA%\ai-agent-gpt-pro-like-chat\work\<run-key>\<role>\<invocation>` である。`run-key` は出力先 directory の完全パスから作り、末尾の名前が同じ別 run とは共有しない。`invocation` は呼び出しごとの識別子であり、`continue` で同じ役割を再実行しても前回の作業ファイルは引き継がない。過去の作業 directory は記録として残し、実行後に削除しない。役割の `result.json` に今回の path と、作られたファイルの相対 path を残す。

実行役は Facade が返す `timeout_seconds` に加え、その秒数 + 30 秒を過ぎてもプロセスが残っている場合に process tree を Kill する。Kill 後に JSON が無いことは失敗ログへ書く。

## 役割へ渡す入力

役割プロンプトは `prompts/*.md` である。実行役が読み、次の材料だけを連結する。

| 役割 | 入力 |
| --- | --- |
| analyst | 役割プロンプトと元質問。他の analyst の出力は含めない |
| disagreement | 役割プロンプト、元質問、全 analyst 出力 |
| critic | 役割プロンプト、元質問、disagreement 成果物。analyst 原文は含めない |
| arbiter | 役割プロンプト、元質問、全 analyst 出力、disagreement、critic を起動した場合はその成果物 |
| adjudicator | arbiter と同じ入力。出力は裁定であり、最終回答ではない |
| executive synthesizer | 役割プロンプト、元質問、裁定。`includeAnalystTextsInSynthesizer` が true のときだけ analyst 原文を足す |
| baseline | 役割プロンプトなしの元質問 |

disagreement の末尾の非空行は `DISAGREEMENT: present` か `DISAGREEMENT: none` だけを認める。`present` のときだけ critic を起動する。行が無い、またはこの2つ以外のときは、critic 以降を起動せず失敗する。

## 成果物

`--out` に指定した directory を新規に作る。既に中身がある場合は失敗する。

- `question.md`
- `config.snapshot.json`
- `trace.log`
- `analysts/<n>/output.md`、`request.json`、`result.json`
- `disagreement.md`
- `critic.md`（`present` のときだけ）
- `adjudication.md`
- `final-pro-like.md`
- `final-fable-like.md`

`baseline` の directory には `question.md`、`config.snapshot.json`、`trace.log`、`final-baseline.md` を書く。

`continue` は `config.snapshot.json` の役割設定と Facade の path を使う。前段ファイルが欠けている場合は後段を始めない。`pro-like` は arbiter だけ、`fable-like` は adjudicator と synthesizer、`both` はその両方を、保存済みの前段から再実行する。

## 失敗

analyst の1本が失敗したときは、残りの Facade CLI を直ちに Kill し、disagreement 以降を始めない。Kill されたプロセスが JSON を出さないことは [docs/cli.md](https://github.com/suusanex/codex_agent_facade/blob/main/docs/cli.md) のとおりであり、trace にその旨を残す。例外は `Exception.ToString()` を trace へ書く。別モデルや少ない analyst 数への切り替えはしない。

## 初回設定

[config/experiment.example.json](../config/experiment.example.json) は `github-copilot` の analyst 3本である。`model`、`timeoutSeconds`、`autoApprove` は各役割に必須で、例では `autoApprove` を false にしている。`facadeCliPath` と `model` は、使う環境の CLI に合わせて書き換える。Cursor、Grok、Codex を混ぜる設定は、この例には入れない。

`autoApprove=false` はツール不使用の証明ではない。Copilot CLI の `--no-custom-instructions` は Facade の JSON から渡せないため、ユーザー全体の custom instructions は Copilot analyst に残りうる。working directory をリポジトリ外にすることで、このリポジトリの `AGENTS.md` を親 directory 探索では読ませない。

## 実質問の比較

self-test は実モデルを呼ばない。baseline と pro-like、同じ前段の pro-like と fable-like を人が比べる手順は [real-question-comparison.md](real-question-comparison.md) に置く。未実施の比較を、起動成功や self-test 成功で代替しない。

## 実装の境界

実行役は BCL の `Process` で Facade CLI を起動する。Facade の C# ソースは参照しない。公開契約がプロセスの標準入出力であり、別リポジトリのソースをこの File-based App へ取り込む必要がないためである。Copilot SDK と OpenClaw command は、この実行役に含まれない。
