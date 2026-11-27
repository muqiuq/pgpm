import { flushPromises } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { mountWithUi } from '@/test/mount'
import OptionsEditor from './OptionsEditor.vue'
import type { OptionForm } from './fieldConfig'

describe('the select options editor (docs/01 §6.1)', () => {
  function editor(initial: OptionForm[]) {
    const options = ref(initial)
    const wrapper = mountWithUi(OptionsEditor, {
      props: {
        modelValue: options.value,
        'onUpdate:modelValue': (v: OptionForm[]) => {
          options.value = v
          void wrapper.setProps({ modelValue: v })
        },
      },
    })
    return { wrapper, options }
  }

  it('makes a new option’s id from its label until the id is typed; a saved option keeps its id', async () => {
    const { wrapper, options } = editor([
      { id: 'lt3', label: '< 3 months', color: '#16a34a', points: 5, archived: false, saved: true },
    ])
    expect(wrapper.get('#opt-id-0').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[aria-label="Remove option < 3 months"]').exists()).toBe(false)

    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Add option')!
      .trigger('click')
    await wrapper.get('#opt-label-1').setValue('4–6 months')
    await flushPromises()
    expect(options.value[1]).toMatchObject({ id: '4_6_months', label: '4–6 months', saved: false })
    await wrapper.get('#opt-id-1').setValue('m4_6')
    await wrapper.get('#opt-label-1').setValue('4 to 6 months')
    expect(options.value[1]!.id).toBe('m4_6')

    await wrapper.get('[aria-label="Archive option < 3 months"]').trigger('click')
    expect(options.value[0]!.archived).toBe(true)
    await wrapper.get('[aria-label="Move option 4 to 6 months up"]').trigger('click')
    expect(options.value.map((o) => o.id)).toEqual(['m4_6', 'lt3'])
    await wrapper.get('[aria-label="Remove option 4 to 6 months"]').trigger('click')
    expect(options.value.map((o) => o.id)).toEqual(['lt3'])
  })
})
