<script setup lang="ts">
// 组件库自检页（/lib-check，不进工作区 tab）：
// 验证 Naive UI 的按钮 / 对话框 / 表格 / 表单控件在「暗色 + 主题色」下显示正确，
// 主题全部由 tokens.css 派生（见 src/theme/naiveTheme.ts）。
// ui-redesign r6：加 PageHeader；新增「图标总览」与「设计令牌」两节，方便一眼回归。
import { h, onMounted, ref } from 'vue'
import {
  NButton,
  NCard,
  NDataTable,
  NInput,
  NModal,
  NSelect,
  NSpace,
  NSwitch,
  NTag,
  useDialog,
  useMessage,
  type DataTableColumns,
} from 'naive-ui'
import AppIcon from '@/components/AppIcon.vue'
import PageHeader from '@/components/PageHeader.vue'
import { ICON_NAMES } from '@/components/icons'

const dialog = useDialog()
const message = useMessage()

const dialogOpen = ref(false)
const modalOpen = ref(false)
const keyword = ref('')
const onlyModified = ref(false)
const dataType = ref('texture')

const typeOptions = [
  { label: '贴图 texture', value: 'texture' },
  { label: '音频 audio', value: 'audio' },
  { label: '文本 text', value: 'text' },
]

// ── 设计令牌总览：色块一律用 var(--lme-*)，值在挂载后从 :root 读一次 ──
const TOKEN_SWATCHES = [
  '--lme-bg-base',
  '--lme-bg-panel',
  '--lme-bg-elevated',
  '--lme-accent',
  '--lme-success',
  '--lme-warning',
  '--lme-error',
  '--lme-info',
  '--lme-text-primary',
  '--lme-text-secondary',
  '--lme-text-muted',
  '--lme-border',
]

const tokenValues = ref<Record<string, string>>({})

onMounted(() => {
  const cs = getComputedStyle(document.documentElement)
  const map: Record<string, string> = {}
  for (const name of TOKEN_SWATCHES) {
    map[name] = cs.getPropertyValue(name).trim()
  }
  tokenValues.value = map
})

interface Row {
  key: number
  name: string
  type: string
  size: string
  state: '未修改' | '已修改' | '已添加' | '已删除'
}

const rows: Row[] = [
  { key: 1, name: 'art/character/faust_lcb/faust_idle', type: 'texture', size: '1.2 MB', state: '未修改' },
  { key: 2, name: 'art/character/faust_lcb/faust_portrait', type: 'texture', size: '640 KB', state: '已修改' },
  { key: 3, name: 'audio/voice/faust_01', type: 'audio', size: '86 KB', state: '已添加' },
  { key: 4, name: 'localize/zh-cn/story_01', type: 'text', size: '12 KB', state: '已删除' },
]

const columns: DataTableColumns<Row> = [
  { title: '名称', key: 'name', ellipsis: { tooltip: true } },
  { title: '类型', key: 'type', width: 90 },
  { title: '大小', key: 'size', width: 90 },
  {
    title: '状态',
    key: 'state',
    width: 100,
    render(row) {
      const kind =
        row.state === '已修改'
          ? 'warning'
          : row.state === '已添加'
            ? 'success'
            : row.state === '已删除'
              ? 'error'
              : 'default'
      return h(
        NTag,
        { size: 'small', type: kind, bordered: false },
        { default: () => row.state },
      )
    },
  },
]

function confirmDialog() {
  dialog.warning({
    title: '确认替换',
    content: '将替换 3 个资源，此操作会写入工程目录。',
    positiveText: '确认替换',
    negativeText: '取消',
    onPositiveClick: () => message.success('已提交替换请求'),
  })
}
</script>

<template>
  <div class="lib-check-shell">
    <PageHeader
      icon="shieldCheck"
      title="组件库自检"
      description="验证 Naive UI 组件在本项目主题下的表现"
      hint="改过 tokens.css 或 naiveTheme.ts 后打开本页看一眼"
      hint-key="lib-check"
    />

    <div class="lib-check">
      <h1 class="lib-check-title">Naive UI 组件</h1>
      <p class="lib-check-sub">
        主题从 <code>tokens.css</code> 派生（暗色 + tokens 主色），此处仅验证显示正确性。
      </p>

      <NSpace vertical :size="16">
        <NCard title="按钮 Button" size="small">
          <NSpace>
            <NButton type="primary">主要按钮</NButton>
            <NButton>次要按钮</NButton>
            <NButton tertiary>第三级</NButton>
            <NButton quaternary>弱按钮</NButton>
            <NButton type="warning">警告</NButton>
            <NButton type="error">危险</NButton>
            <NButton type="success">成功</NButton>
            <NButton disabled>禁用</NButton>
            <NButton size="small" type="primary" @click="confirmDialog">打开对话框</NButton>
            <NButton size="small" @click="modalOpen = true">打开模态</NButton>
          </NSpace>
        </NCard>

        <NCard title="表单 Form" size="small">
          <NSpace align="center">
            <NInput
              v-model:value="keyword"
              placeholder="搜索资源名称"
              style="width: 220px"
              clearable
            />
            <NSelect v-model:value="dataType" :options="typeOptions" style="width: 160px" />
            <NSwitch v-model:value="onlyModified" />
            <span class="lib-check-hint">仅显示已修改</span>
            <NTag type="info" size="small" :bordered="false">tag 胶囊</NTag>
            <NTag type="warning" size="small" :bordered="false">已修改</NTag>
          </NSpace>
        </NCard>

        <NCard title="表格 DataTable" size="small">
          <NDataTable
            :columns="columns"
            :data="rows"
            size="small"
            :bordered="false"
            :single-line="false"
          />
        </NCard>

        <NCard title="图标总览" size="small">
          <template #header-extra>
            <span class="lib-check-hint">共 {{ ICON_NAMES.length }} 个</span>
          </template>
          <div class="icon-grid">
            <div v-for="name in ICON_NAMES" :key="name" class="icon-cell" :title="name">
              <AppIcon :name="name" :size="18" />
              <span class="icon-cell-name lme-mono">{{ name }}</span>
            </div>
          </div>
        </NCard>

        <NCard title="设计令牌" size="small">
          <template #header-extra>
            <span class="lib-check-hint">色块一律取 var(--lme-*)</span>
          </template>
          <div class="token-grid">
            <div v-for="name in TOKEN_SWATCHES" :key="name" class="token-item">
              <span class="token-swatch" :style="{ background: `var(${name})` }" />
              <span class="token-name lme-mono">{{ name }}</span>
              <span class="token-value lme-mono">{{ tokenValues[name] || '—' }}</span>
            </div>
          </div>
        </NCard>
      </NSpace>
    </div>

    <NModal v-model:show="modalOpen" preset="card" title="示例模态框" style="width: 420px">
      <span>模态内容：验证遮罩、标题与按钮在暗色主题下的对比度。</span>
      <template #footer>
        <NSpace justify="end">
          <NButton size="small" @click="modalOpen = false">取消</NButton>
          <NButton size="small" type="primary" @click="modalOpen = false">确定</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.lib-check-shell {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.lib-check {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: var(--lme-gap-xl);
  max-width: 1100px;
  width: 100%;
  margin: 0 auto;
}

.lib-check-title {
  margin: 0 0 var(--lme-gap-xs);
  font-size: var(--lme-font-size-lg);
  font-weight: var(--lme-font-weight-semibold);
  color: var(--lme-text-primary);
}

.lib-check-sub {
  margin: 0 0 var(--lme-gap-lg);
  font-size: var(--lme-font-size-sm);
  color: var(--lme-text-muted);
}

.lib-check-hint {
  font-size: var(--lme-font-size-xs);
  color: var(--lme-text-muted);
}

/* ── 图标总览：每格「图标 + 图标名」，扫一眼即可看出哪个图标没渲染出来 ── */
.icon-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(112px, 1fr));
  gap: var(--lme-gap-xs);
}

.icon-cell {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  background: var(--lme-bg-inset);
  color: var(--lme-text-secondary);
  min-width: 0;
}

.icon-cell-name {
  font-size: var(--lme-font-size-2xs);
  color: var(--lme-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 设计令牌：色块 + 变量名 + 当前值 ── */
.token-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: var(--lme-gap-xs);
}

.token-item {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  padding: var(--lme-gap-xs) var(--lme-gap-sm);
  border: 1px solid var(--lme-border);
  border-radius: var(--lme-radius-sm);
  background: var(--lme-bg-inset);
  min-width: 0;
}

.token-swatch {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  border: 1px solid var(--lme-border-strong);
  border-radius: var(--lme-radius-xs);
}

.token-name {
  font-size: var(--lme-font-size-2xs);
  color: var(--lme-text-secondary);
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.token-value {
  font-size: var(--lme-font-size-2xs);
  color: var(--lme-text-muted);
  flex-shrink: 0;
}
</style>
