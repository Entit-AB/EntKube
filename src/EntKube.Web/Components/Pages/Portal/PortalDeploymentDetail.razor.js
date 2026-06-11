export function getElementValue(id) {
    const el = document.getElementById(id);
    return el ? el.value : '';
}
