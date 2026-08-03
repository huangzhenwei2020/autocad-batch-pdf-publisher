import { describe, expect, it } from "vitest";
import { createProjectMessage, createReadyMessage, loadStoredWorkspace } from "./editor-app";
import {
  applyBuildingTemplate,
  archiveReviewResult,
  createBlankWorkspace,
  createInitialWorkspace,
  getReviewFingerprint,
  getWorkspaceIssues,
  recordFieldChange,
  updateReviewIssueAction,
} from "./editor-model";
import {
  getReviewReportPageTitles,
  type RuleReviewResult,
} from "./rule-review";
import {
  buildWorkspaceFromWizard,
  createProjectWizardDraft,
  validateWizardStep,
} from "./project-wizard";
import { compareWorkspaces } from "./project-history";
import { compareReviewRecords } from "./review-comparison";
import {
  applyTableFormula,
  bindTableCellToProjectField,
  createProfessionalTableTemplate,
  evaluateTableFormula,
  mergeSelectedCells,
  pasteTableCells,
  recalculateTechnicalTable,
  setTableCellValue,
  splitMergedCell,
  synchronizeBoundTableCells,
  tableToCsv,
  validateProfessionalTable,
} from "./professional-tables";

function createRuleIssue(ruleId: string, fieldPath: string) {
  return {
    issueId: `${ruleId}-issue`,
    ruleId,
    severity: "error" as const,
    title: `${ruleId} 问题`,
    message: "测试问题",
    standardCode: "",
    clauseReference: "",
    targetNodeId: "",
    targetFieldPath: fieldPath,
    evidence: "测试证据",
    suggestedAction: "测试处理",
    requiresProfessionalConfirmation: false,
  };
}

describe("architecture specification editor", () => {
  it("creates the first two professional table templates", () => {
    const indicators = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    const waterproof = createProfessionalTableTemplate("waterproofDesign", "表2");
    expect(indicators.title).toBe("主要技术经济指标表");
    expect(indicators.columns.map((column) => column.title)).toContain("设计值");
    expect(waterproof.title).toBe("防水设计表");
    expect(waterproof.columns.map((column) => column.title)).toContain("防水材料");
    expect(indicators.repeatHeader).toBe(true);
  });

  it("pastes an Excel cell range and automatically adds rows", () => {
    const table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    const pasted = pasteTableCells(
      { ...table, rows: table.rows.slice(0, 1) },
      0,
      0,
      "总建筑面积\t100\t98\n容积率\t2.5\t2.4",
    );
    expect(pasted.rows).toHaveLength(2);
    expect(pasted.rows[1].cells[0].displayValue).toBe("容积率");
    expect(pasted.rows[1].cells[2].numericValue).toBe(2.4);
  });

  it("recalculates the technical indicator difference with column precision", () => {
    let table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    table = setTableCellValue(table, 0, 1, "100");
    table = setTableCellValue(table, 0, 2, "98.125");
    table = recalculateTechnicalTable(table);
    expect(table.rows[0].cells[5].displayValue).toBe("-1.88");
    expect(table.rows[0].cells[5].formula).toBe("design - planning");
  });

  it("validates required waterproof cells and layer count", () => {
    let table = createProfessionalTableTemplate("waterproofDesign", "表2");
    expect(validateProfessionalTable(table).length).toBeGreaterThan(0);
    table = setTableCellValue(table, 0, 4, "1.5");
    expect(
      validateProfessionalTable(table).some((issue) => issue.message.includes("正整数")),
    ).toBe(true);
  });

  it("exports quoted UTF-8 friendly CSV content", () => {
    let table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    table = setTableCellValue(table, 0, 0, '总建筑面积,"复核"');
    const csv = tableToCsv(table);
    expect(csv).toContain('"指标名称"');
    expect(csv).toContain('"总建筑面积,""复核"""');
  });

  it("restores professional tables from a saved workspace", () => {
    const workspace = {
      ...createInitialWorkspace(),
      tables: [createProfessionalTableTemplate("waterproofDesign", "表2")],
    };
    const restored = loadStoredWorkspace({
      getItem: () => JSON.stringify(workspace),
    });
    expect(restored.tables).toHaveLength(1);
    expect(restored.tables?.[0].title).toBe("防水设计表");
    expect(restored.tables?.[0].rows).toHaveLength(2);
  });

  it("merges and splits a rectangular cell range without losing hidden values", () => {
    const table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    const originalHiddenValue = table.rows[1].cells[0].displayValue;
    const merged = mergeSelectedCells(table, {
      startRow: 0,
      startColumn: 0,
      endRow: 1,
      endColumn: 1,
    });
    expect(merged.rows[0].cells[0].rowSpan).toBe(2);
    expect(merged.rows[0].cells[0].columnSpan).toBe(2);
    expect(merged.rows[1].cells[0].rowSpan).toBe(0);
    const split = splitMergedCell(merged, 0, 0);
    expect(split.rows[1].cells[0].rowSpan).toBe(1);
    expect(split.rows[1].cells[0].displayValue).toBe(originalHiddenValue);
  });

  it("rejects a second merge over an existing merged range", () => {
    const table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    const merged = mergeSelectedCells(table, {
      startRow: 0,
      startColumn: 0,
      endRow: 0,
      endColumn: 1,
    });
    expect(() =>
      mergeSelectedCells(merged, {
        startRow: 0,
        startColumn: 0,
        endRow: 1,
        endColumn: 1,
      }),
    ).toThrow("已合并单元格");
  });

  it("evaluates only whitelisted table formulas", () => {
    let table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    table = setTableCellValue(table, 0, 1, "100");
    table = setTableCellValue(table, 0, 2, "98.125");
    const evaluation = evaluateTableFormula(
      "ROUND([design] - [planning], 2)",
      table.rows[0],
    );
    expect(evaluation.result).toBe(-1.88);
    expect(evaluation.inputs).toEqual({ design: 98.125, planning: 100 });
    expect(() => evaluateTableFormula("POW([design], 2)", table.rows[0])).toThrow(
      "不在白名单",
    );
  });

  it("supports IF comparisons and records formula audit information", () => {
    let table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    table = setTableCellValue(table, 0, 1, "100");
    table = setTableCellValue(table, 0, 2, "98");
    table = applyTableFormula(
      table,
      0,
      5,
      "IF([design] >= [planning], 1, 0)",
    );
    expect(table.rows[0].cells[5].displayValue).toBe("0.00");
    expect(table.formulaAudits).toHaveLength(1);
    expect(table.formulaAudits[0].formulaVersion).toBe("1");
    expect(table.formulaAudits[0].isManuallyOverridden).toBe(false);
  });

  it("binds a table cell to the shared project field value and source", () => {
    const workspace = createInitialWorkspace();
    const field = workspace.fields.find(
      (item) => item.path === "building.totalFloorArea",
    )!;
    const table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    const bound = bindTableCellToProjectField(table, 0, 2, field);
    expect(bound.rows[0].cells[2].fieldPath).toBe(field.path);
    expect(bound.rows[0].cells[2].displayValue).toBe(field.value);
    expect(bound.rows[0].cells[2].source).toBe(field.source);
  });

  it("creates interior finish, safety and accessibility table templates", () => {
    const finish = createProfessionalTableTemplate("interiorFinish", "表3");
    const safety = createProfessionalTableTemplate("buildingSafetyMeasures", "表4");
    const accessibility = createProfessionalTableTemplate(
      "accessibilityFacilities",
      "表5",
    );
    expect(finish.title).toBe("室内装修做法表");
    expect(finish.rows.map((row) => row.cells[0].displayValue)).toContain("卫生间");
    expect(safety.title).toBe("建筑安全措施表");
    expect(safety.columns.map((column) => column.title)).toContain("安全风险");
    expect(accessibility.title).toBe("无障碍设施表");
    expect(accessibility.rows).toHaveLength(5);
  });

  it("synchronizes stale bound cells and recalculates technical differences", () => {
    const workspace = createInitialWorkspace();
    const originalField = workspace.fields.find(
      (item) => item.path === "building.totalFloorArea",
    )!;
    let table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    table = setTableCellValue(table, 0, 1, "100");
    table = bindTableCellToProjectField(table, 0, 2, originalField);
    const fields = workspace.fields.map((field) =>
      field.path === originalField.path
        ? { ...field, value: "42000.00", source: "更新后的面积统计" }
        : field,
    );
    const result = synchronizeBoundTableCells([table], fields);
    expect(result.updatedCount).toBe(1);
    expect(result.tables[0].rows[0].cells[2].displayValue).toBe("42000.00");
    expect(result.tables[0].rows[0].cells[5].displayValue).toBe("41900.00");
  });

  it("does not overwrite manually changed bound cells without confirmation", () => {
    const workspace = createInitialWorkspace();
    const field = workspace.fields.find(
      (item) => item.path === "building.totalFloorArea",
    )!;
    let table = createProfessionalTableTemplate("technicalEconomicIndicators", "表1");
    table = bindTableCellToProjectField(table, 0, 2, field);
    table = setTableCellValue(table, 0, 2, "人工值");
    const changedFields = workspace.fields.map((item) =>
      item.path === field.path ? { ...item, value: "48000.00" } : item,
    );
    const safe = synchronizeBoundTableCells([table], changedFields, false);
    expect(safe.skippedConflicts).toHaveLength(1);
    expect(safe.tables[0].rows[0].cells[2].displayValue).toBe("人工值");
    const overwritten = synchronizeBoundTableCells([table], changedFields, true);
    expect(overwritten.skippedConflicts).toHaveLength(0);
    expect(overwritten.tables[0].rows[0].cells[2].displayValue).toBe("48000.00");
  });

  it("reports field paths that no longer exist in the project", () => {
    let table = createProfessionalTableTemplate("waterproofDesign", "表2");
    table = {
      ...table,
      rows: table.rows.map((row, rowIndex) =>
        rowIndex === 0
          ? {
              ...row,
              cells: row.cells.map((cell, columnIndex) =>
                columnIndex === 0
                  ? { ...cell, fieldPath: "removed.field.path" }
                  : cell,
              ),
            }
          : row,
      ),
    };
    const result = synchronizeBoundTableCells([table], createInitialWorkspace().fields);
    expect(result.missingFieldPaths).toEqual(["removed.field.path"]);
  });

  it("announces the phase-two editor to the host", () => {
    expect(createReadyMessage("sample-id")).toEqual({
      protocolVersion: 1,
      messageId: "sample-id",
      type: "editor.ready",
      payload: { phase: 2 },
    });
  });

  it("creates a project save message for the CAD host", () => {
    const message = createProjectMessage("project.save", { createSnapshot: true });
    expect(message.type).toBe("project.save");
    expect(message.payload.createSnapshot).toBe(true);
    expect(message.protocolVersion).toBe(1);
  });

  it("creates a version history request for the CAD host", () => {
    const message = createProjectMessage("project.historyLoad", { snapshotPath: "sample.jzsmproj" });
    expect(message.type).toBe("project.historyLoad");
    expect(message.payload.snapshotPath).toBe("sample.jzsmproj");
  });

  it("creates a national foundation review request", () => {
    const workspace = createInitialWorkspace();
    const message = createProjectMessage("review.run", { workspace });
    expect(message.type).toBe("review.run");
    expect(message.payload.workspace).toBe(workspace);
  });

  it("archives review results without changing the project data fingerprint", () => {
    const workspace = createInitialWorkspace();
    const result: RuleReviewResult = {
      packageId: "CN-TEST",
      packageVersion: "1.0.0",
      packageDisplayName: "测试规则包",
      packageStatus: "Draft",
      packageVerifiedAt: "2026-07-30T00:00:00+08:00",
      executedAt: "2026-07-30T12:00:00+08:00",
      localRulesLoaded: false,
      scopeNotice: "测试",
      issues: [],
    };
    const fingerprint = getReviewFingerprint(workspace);
    const archived = archiveReviewResult(workspace, result);
    expect(archived.reviewRecords).toHaveLength(1);
    expect(archived.reviewRecords?.[0].projectFingerprint).toBe(fingerprint);
    expect(getReviewFingerprint(archived)).toBe(fingerprint);
  });

  it("marks a review fingerprint stale after project data changes", () => {
    const workspace = createInitialWorkspace();
    const changed = { ...workspace, projectName: "修改后的项目名称" };
    expect(getReviewFingerprint(changed)).not.toBe(getReviewFingerprint(workspace));
  });

  it("keeps signoff defaults outside the design data fingerprint", () => {
    const workspace = createInitialWorkspace();
    const withSignoff = {
      ...workspace,
      reviewSignoff: {
        organization: "测试设计院",
        projectManager: "项目负责人",
        preparedBy: "编制人",
        checkedBy: "校对人",
        approvedBy: "审核人",
        reportNumber: "",
      },
    };
    expect(getReviewFingerprint(withSignoff)).toBe(getReviewFingerprint(workspace));
  });

  it("freezes signoff information and generates a report number in the archive", () => {
    const workspace = {
      ...createInitialWorkspace(),
      reviewSignoff: {
        organization: "测试设计院",
        projectManager: "张三",
        preparedBy: "李四",
        checkedBy: "王五",
        approvedBy: "赵六",
        reportNumber: "",
      },
    };
    const result: RuleReviewResult = {
      packageId: "CN-TEST",
      packageVersion: "1.0.0",
      packageDisplayName: "测试规则",
      packageStatus: "Draft",
      packageVerifiedAt: "",
      executedAt: "2026-07-30T11:00:00+08:00",
      localRulesLoaded: false,
      scopeNotice: "",
      issues: [],
    };
    const record = archiveReviewResult(workspace, result).reviewRecords![0];
    expect(record.reviewSignoff.organization).toBe("测试设计院");
    expect(record.reviewSignoff.projectManager).toBe("张三");
    expect(record.reviewSignoff.reportNumber).toMatch(/^BPP-JZYS-20260730-/);
  });

  it("persists issue handling status and review responsibility", () => {
    const workspace = createInitialWorkspace();
    const result: RuleReviewResult = {
      packageId: "CN-TEST",
      packageVersion: "1.0.0",
      packageDisplayName: "测试规则包",
      packageStatus: "Draft",
      packageVerifiedAt: "2026-07-30T00:00:00+08:00",
      executedAt: "2026-07-30T12:00:00+08:00",
      localRulesLoaded: false,
      scopeNotice: "测试",
      issues: [{
        issueId: "issue-1",
        ruleId: "RULE-1",
        severity: "error",
        title: "测试问题",
        message: "测试",
        standardCode: "",
        clauseReference: "",
        targetNodeId: "",
        targetFieldPath: "project.location",
        evidence: "未填写",
        suggestedAction: "补充",
        requiresProfessionalConfirmation: true,
      }],
    };
    const record = archiveReviewResult(workspace, result).reviewRecords![0];
    const updated = updateReviewIssueAction(record, "issue-1", {
      status: "resolved",
      owner: "设计人",
      comment: "已补充建设地点",
      reviewer: "校对人",
    });
    expect(updated.issueActions[0].status).toBe("resolved");
    expect(updated.issueActions[0].owner).toBe("设计人");
    expect(updated.issueActions[0].reviewer).toBe("校对人");
  });

  it("builds deterministic report pages and continues issue detail pages", () => {
    expect(getReviewReportPageTitles(0)).toEqual([
      "封面与预审结论",
      "项目与审查范围",
      "问题明细 1/1",
      "问题处理记录",
      "规则索引与使用声明",
    ]);
    expect(getReviewReportPageTitles(9)).toHaveLength(7);
    expect(getReviewReportPageTitles(9)[4]).toBe("问题明细 3/3");
  });

  it("compares review archives by stable rule target instead of random issue id", () => {
    const workspace = createInitialWorkspace();
    const olderResult: RuleReviewResult = {
      packageId: "CN-TEST",
      packageVersion: "1.0.0",
      packageDisplayName: "测试规则",
      packageStatus: "Draft",
      packageVerifiedAt: "",
      executedAt: "2026-07-30T10:00:00+08:00",
      localRulesLoaded: false,
      scopeNotice: "",
      issues: [
        createRuleIssue("RULE-REMOVED", "project.location"),
        createRuleIssue("RULE-PERSISTENT", "building.height"),
      ],
    };
    const newerResult: RuleReviewResult = {
      ...olderResult,
      packageVersion: "1.1.0",
      executedAt: "2026-07-30T11:00:00+08:00",
      issues: [
        { ...createRuleIssue("RULE-PERSISTENT", "building.height"), issueId: "new-random-id" },
        createRuleIssue("RULE-ADDED", "fire.resistanceRating"),
      ],
    };
    const older = archiveReviewResult(workspace, olderResult).reviewRecords![0];
    const newer = archiveReviewResult(workspace, newerResult).reviewRecords![0];
    const comparison = compareReviewRecords(newer, older);
    expect(comparison.older.result.packageVersion).toBe("1.0.0");
    expect(comparison.added.map((item) => item.issue.ruleId)).toEqual(["RULE-ADDED"]);
    expect(comparison.persistent.map((item) => item.issue.ruleId)).toEqual(["RULE-PERSISTENT"]);
    expect(comparison.noLongerDetected.map((item) => item.issue.ruleId)).toEqual(["RULE-REMOVED"]);
  });

  it("loads a valid saved workspace", () => {
    const sample = createInitialWorkspace();
    const loaded = loadStoredWorkspace({
      getItem: () => JSON.stringify({ ...sample, projectName: "已保存项目" }),
    });
    expect(loaded.projectName).toBe("已保存项目");
    expect(loaded.sections).toHaveLength(14);
    expect(loaded.fields.every((field) => field.locked === false)).toBe(true);
  });

  it("falls back to the sample when local data is invalid", () => {
    const loaded = loadStoredWorkspace({ getItem: () => "{broken" });
    expect(loaded.projectName).toContain("示例办公项目");
  });

  it("reports only unconfirmed project fields", () => {
    const workspace = createInitialWorkspace();
    const issues = getWorkspaceIssues(workspace);
    expect(issues.some((issue) => issue.fieldPath === "fire.classification")).toBe(true);
    expect(issues.some((issue) => issue.fieldPath === "building.height")).toBe(false);
  });

  it("activates conditional sections from explicit project conditions", () => {
    const workspace = createInitialWorkspace();
    const withoutSpecialist = applyBuildingTemplate({
      ...workspace,
      buildingType: "工业建筑",
      features: {
        ...workspace.features!,
        hasCurtainWall: false,
        hasElevator: false,
        hasSpecialistDesign: false,
      },
    });
    expect(withoutSpecialist.sections.find((section) => section.id === "specialist")?.enabled).toBe(false);
    expect(withoutSpecialist.sections.find((section) => section.id === "elevators")?.enabled).toBe(false);

    const withSpecialist = applyBuildingTemplate({
      ...withoutSpecialist,
      features: { ...withoutSpecialist.features!, hasSpecialistDesign: true },
    });
    expect(withSpecialist.sections.find((section) => section.id === "specialist")?.enabled).toBe(true);
  });

  it("uses a lighter section template during proposal design", () => {
    const workspace = applyBuildingTemplate({
      ...createInitialWorkspace(),
      designStage: "方案设计",
    });
    expect(workspace.sections.find((section) => section.id === "materials")?.requirement).toBe("optional");
    expect(workspace.sections.find((section) => section.id === "fire")?.requirement).toBe("required");
  });

  it("does not report optional template fields as missing", () => {
    const workspace = applyBuildingTemplate({
      ...createInitialWorkspace(),
      features: { ...createInitialWorkspace().features!, isGreenBuilding: false },
      fields: createInitialWorkspace().fields.map((field) =>
        field.path === "green.targetRating" ? { ...field, state: "unknown" as const } : field,
      ),
    });
    expect(getWorkspaceIssues(workspace).some((issue) => issue.fieldPath === "green.targetRating")).toBe(false);
  });

  it("creates a blank project without carrying sample values", () => {
    const workspace = createBlankWorkspace();
    expect(workspace.projectName).toBe("");
    expect(workspace.fields.every((field) => field.value === "")).toBe(true);
    expect(workspace.fields.every((field) => field.state === "unknown")).toBe(true);
  });

  it("validates required wizard data before advancing", () => {
    const draft = createProjectWizardDraft();
    expect(validateWizardStep(0, draft)).toContain("请填写项目名称");
    expect(validateWizardStep(1, draft)).toContain("总建筑面积应为大于 0 的数字");
  });

  it("builds a pending project and activates conditional chapters", () => {
    const draft = {
      ...createProjectWizardDraft(),
      projectName: "向导测试项目",
      province: "广西壮族自治区",
      city: "南宁市",
      district: "青秀区",
      buildingType: "办公建筑",
      totalFloorArea: "12500",
      buildingHeight: "36",
      aboveGroundFloors: "9",
      undergroundFloors: "1",
      features: {
        ...createProjectWizardDraft().features,
        hasCurtainWall: true,
      },
    };
    const workspace = buildWorkspaceFromWizard(draft);
    expect(workspace.location).toBe("广西壮族自治区南宁市青秀区");
    expect(workspace.features?.hasBasement).toBe(true);
    expect(workspace.sections.find((section) => section.id === "doors-curtain-wall")?.enabled).toBe(true);
    expect(workspace.fields.find((field) => field.path === "project.projectName")?.state).toBe("pending");
    expect(JSON.stringify(workspace.sections)).toContain("向导测试项目");
  });

  it("coalesces continuous edits to the same field", () => {
    const workspace = createInitialWorkspace();
    const first = recordFieldChange(
      workspace,
      "project.projectName",
      "value",
      "原项目",
      "新",
      "用户修改字段值",
    );
    const second = recordFieldChange(
      first,
      "project.projectName",
      "value",
      "新",
      "新项目",
      "用户修改字段值",
    );
    expect(second.fieldChanges).toHaveLength(1);
    expect(second.fieldChanges?.[0].oldValue).toBe("原项目");
    expect(second.fieldChanges?.[0].newValue).toBe("新项目");
  });

  it("detects a deterministic basement conflict", () => {
    const workspace = {
      ...createInitialWorkspace(),
      features: {
        ...createInitialWorkspace().features!,
        hasBasement: false,
      },
    };
    expect(getWorkspaceIssues(workspace).some((issue) => issue.id === "conflict:basement-disabled")).toBe(true);
  });

  it("detects a locked field with an unconfirmed state", () => {
    const workspace = {
      ...createInitialWorkspace(),
      fields: createInitialWorkspace().fields.map((field) =>
        field.path === "building.height"
          ? { ...field, locked: true, state: "pending" as const }
          : field,
      ),
    };
    expect(getWorkspaceIssues(workspace).some((issue) => issue.id === "conflict:locked-state:building.height")).toBe(true);
  });

  it("compares metadata, fields and section content against a snapshot", () => {
    const snapshot = createInitialWorkspace();
    const current = {
      ...snapshot,
      projectName: "修改后的项目",
      fields: snapshot.fields.map((field) =>
        field.path === "building.height" ? { ...field, value: "60.00" } : field,
      ),
      sections: snapshot.sections.map((section) =>
        section.id === "fire" ? { ...section, reviewState: "ready" as const, content: { type: "doc", content: [] } } : section,
      ),
    };
    const difference = compareWorkspaces(current, snapshot);
    expect(difference.metadata.map((item) => item.label)).toContain("项目名称");
    expect(difference.fields.map((item) => item.label)).toContain("建筑高度");
    expect(difference.sections.some((item) => item.includes("建筑防火设计"))).toBe(true);
  });
});
