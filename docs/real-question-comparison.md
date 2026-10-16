# 実質問による baseline / pro-like / fable-like の比較

この文書は、後で行う手動比較の手順である。ここまでの self-test 成功や、この文書の存在は、実サブスクリプションで実用価値が確認できたことを意味しない。

## 実施しない理由と実施主体

エージェントは、GitHub Copilot の利用枠を消費する実質問をこの確認の代わりに実行しない。ログイン、その時点で有効な model id、1役割あたり最大 1200 秒の待ち時間が必要であり、生成文の採点者にはならない。

圭太が、purpose review と PR 上のレビュー対応のあと、自分で選んだ実質問に対して行う。

## 準備

1. `config/experiment.example.json` をコピーし、`facadeCliPath` を起動できる `CodexAgentFacadeCli` にする。`model` は、その時点の GitHub Copilot で使える id に書き換える。初回は agent を `github-copilot` の3本のまま、`autoApprove` は false、`fast` は書かない。
2. 質問ファイルを置く。実行前に、その質問への妥当な結論と、大外しとみなす結論を短く書いておく。実行結果を見てから基準を作らない。
3. 空の出力先で `dotnet run --file src/ProLikeRunner.cs -- run --config <config> --question <question> --out <empty-dir>` を実行する。
4. 別の空ディレクトリで `baseline --config <同じconfig> --question <同じquestion> --out <別dir>` を実行する。baseline は run の前段を再利用しない。
5. 保存済み run で `continue --run-dir <run-dir> --downstream both` を実行する。analyst、disagreement、critic の成果物時刻が変わらず、trace に前段の再実行が無いことを見る。

プロトコルの確認:

- `run` が exit 0 で、`final-pro-like.md` と `final-fable-like.md` がある。
- `roles/analyst-*/prompt.md` に、他の analyst の出力本文が無い。
- disagreement の末尾の非空行が `DISAGREEMENT: present` または `DISAGREEMENT: none` である。present のときだけ `critic.md`、none のとき `critic.skipped` があり `critic.md` は無い。
- `includeAnalystTextsInSynthesizer` が false のとき、synthesizer の prompt に analyst 原文が無い。
- 各役割の `result.json` にある作業 directory は呼び出しごとに異なり、前回の作業ファイルを含まない。

## 実用価値の見方

自動採点や数値の閾値は使わない。各観点を次の4つで書く。

- 改善
- 改善なし
- 悪化
- 判断不能

判断不能のときは、何が観察できなかったかを書く。

baseline の `final-baseline.md` と、同じ質問の `final-pro-like.md`:

- 探索: 質問の主要な分岐、前提、反証候補を、baseline より多く扱っているか。
- 反証: 有力な代替仮説や前提の誤りを、結論へ反映しているか。
- 大外し: 実行前に書いた妥当な結論から見て外れているか。baseline より減ったか、同じか、増えたか。

同じ run の `final-pro-like.md` と `final-fable-like.md`:

- 重要度: 争点の重みづけが、単なる要約を超えて判断の決め手になっているか。
- 追加の含意: adjudicator の裁定から、synthesizer が行動や選択条件を導いているか。analyst 原文の再掲だけになっていないか。
- 判断可能性: 採否、または採否を分ける条件が読めるか。

1件の記録でプロトコルの採否は決めない。少なくとも1件を残し、大外しが減ったかどうかは同じ手順の複数件が揃ってから見る。

## 記録と戻し方

結果は Issue #1 または #2 のコメントに残す。プロトコル違反（入力の分離、disagreement マーカー、作業 directory の引き継ぎ、失敗時の別経路）があれば runner を修正し、`dotnet run --file src/ProLikeRunner.cs -- self-test` のあと、同じ質問手順へ戻る。実用価値が改善なし、悪化、または判断不能でも、runner の起動成功としては扱わない。その場合はプロンプトまたは段取りの変更として issue に残す。
